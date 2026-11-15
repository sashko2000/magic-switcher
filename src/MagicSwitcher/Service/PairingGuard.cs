using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MagicSwitcher.Bluetooth;
using MagicSwitcher.Infrastructure;

namespace MagicSwitcher.Service;

/// <summary>
/// Keeps the selected devices (keyboards) working on this computer, including at the sign-in screen.
/// <para>
/// A multi-host keyboard like Apple Magic Keyboard keeps the key of one host only: once it was used with
/// another device, it rejects this PC and has to be paired again. The guard does that without the Windows
/// dialog: it answers the pairing request itself (only for the selected devices and only while a "session"
/// is running), pairs with bonding so the key is stored, and enables the HID service.
/// </para>
/// <para>
/// A session starts on an event (service start, sign-in, unlock, wake, display on, a request from the tray).
/// It first gives the device time to connect by itself (knocking right after boot breaks a connection that is
/// coming up and makes Windows drop the key), then "knocks" periodically until the device works or time is up.
/// The guard never knocks outside a session: when the user hands the keyboard to a phone on purpose,
/// knocking would take it back or break the pairing.
/// </para>
/// </summary>
internal sealed class PairingGuard : IDisposable
{
    private const uint WmQuit = 0x0012;

    /// <summary>How often to try pairing while the device has no pairing.</summary>
    private static readonly TimeSpan PairingInterval = TimeSpan.FromSeconds(5);
    private const uint WmApp = 0x8000;


    private readonly Thread _loop;
    private uint _loopThreadId;
    private readonly ManualResetEventSlim _loopReady = new();
    private readonly ConcurrentQueue<Action> _loopActions = new();
    private readonly Dictionary<ulong, (NativeBluetooth.PairingRegistration Registration, int Count)> _registrations = []; // loop thread only

    private readonly Lock _sync = new();
    private readonly Dictionary<ulong, Session> _sessions = [];
    private IReadOnlyList<GuardedDevice> _devices = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly System.Threading.Timer _stateTimer;
    private readonly Dictionary<ulong, string> _lastStates = []; // state timer only
    private int _loggingStates;

    public PairingGuard()
    {
        _stateTimer = new System.Threading.Timer(_ => LogStateChanges(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        // Pairing callbacks are delivered through the message queue of the thread that registered them.
        _loop = new Thread(RunLoop) { IsBackground = true, Name = "Pairing message loop" };
        _loop.SetApartmentState(ApartmentState.STA);
        _loop.Start();
        _loopReady.Wait();
    }

    /// <summary>
    /// Called after a guarded device was paired again. Windows then installs the keyboard anew, and the Apple driver
    /// resets its top row setting, so the service re-applies it.
    /// </summary>
    public Action? AfterPairing { get; set; }

    /// <summary>
    /// What the guard is doing about a device that does not work, for the status window on the sign-in screen.
    /// Nothing is reported for a device that simply connects by itself.
    /// </summary>
    public Action<GuardedDevice, KeyboardPhase>? StatusChanged { get; set; }

    public IReadOnlyList<GuardedDevice> Devices
    {
        get { lock (_sync) { return _devices; } }
        set { lock (_sync) { _devices = value; } }
    }

    /// <summary>Starts a session for every guarded device that is not working yet (no-op if one is running).</summary>
    public void Trigger(ServiceTrigger trigger)
    {
        var plan = Plan.For(trigger);
        foreach (var device in Devices)
        {
            var state = NativeBluetooth.GetState(device.Address);
            string hid = DescribeHid(device, state);
            bool types = Types(device, state); // outside the lock: counting HID collections may take a moment
            lock (_sync)
            {
                if (_sessions.ContainsKey(device.Address))
                {
                    Log.Info($"[Guard] {trigger}: '{Name(device)}' {state}, {hid}: already watching");
                    continue;
                }

                if (types)
                {
                    Log.Info($"[Guard] {trigger}: '{Name(device)}' {state}, {hid}: works, nothing to do");
                    continue;
                }

                Log.Info($"[Guard] {trigger}: '{Name(device)}' is not working ({state}, {hid}): watching for {plan.Duration.TotalMinutes:0} min");
                StartSession(device, plan, trigger.ToString());
            }
        }
    }

    /// <summary>
    /// Brings the device to this PC now (the tray's "Connect"): knocks right away and every 10 s.
    /// Returns true when it works. Joins a session that is already running.
    /// </summary>
    public Task<bool> PullAsync(GuardedDevice device, TimeSpan duration, bool force, Action<string>? progress)
    {
        var state = NativeBluetooth.GetState(device.Address);
        bool types = !force && Types(device, state); // outside the lock: counting HID collections may take a moment
        lock (_sync)
        {
            if (_sessions.TryGetValue(device.Address, out var running))
            {
                Log.Info($"[Guard] Request: connecting '{Name(device)}' ({state}): joining the running session for up to {duration.TotalSeconds:0} s");
                running.Expedite(duration, progress);
                // The session may go on for minutes (e.g. the one started at startup); the tray waits only this long.
                return WaitAsync(running, device, duration);
            }

            if (types)
            {
                return Task.FromResult(true);
            }

            Log.Info($"[Guard] Request: connecting '{Name(device)}' ({state}) for up to {duration.TotalSeconds:0} s");
            return StartSession(device, Plan.Request(duration), "Request", progress, force).Completion;
        }
    }

    private static async Task<bool> WaitAsync(Session session, GuardedDevice device, TimeSpan duration)
    {
        try
        {
            return await session.Completion.WaitAsync(duration);
        }
        catch (TimeoutException)
        {
            return Types(device, NativeBluetooth.GetState(device.Address));
        }
    }

    private Session StartSession(GuardedDevice device, Plan plan, string reason, Action<string>? progress = null, bool forceFirstKnock = false)
    {
        var session = new Session(device, plan, progress) { ForceFirstKnock = forceFirstKnock };
        _sessions[device.Address] = session;
        _ = Task.Run(async () =>
        {
            bool working = false;
            try
            {
                await RunOnLoopAsync(() => AddRegistration(device.Address));
                working = await RunSessionAsync(session, _stopping.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error($"[Guard] Session for '{Name(device)}' failed", ex);
            }
            finally
            {
                lock (_sync)
                {
                    _sessions.Remove(device.Address);
                }

                try
                {
                    await RunOnLoopAsync(() => RemoveRegistration(device.Address));
                }
                catch (Exception ex)
                {
                    Log.Error("[Guard] Failed to unregister", ex);
                }

                Log.Info($"[Guard] {reason}: '{Name(device)}' {(working ? "works" : "still not working, stopped watching")}: {NativeBluetooth.GetState(device.Address)}");
                if (!working)
                {
                    Report(session, KeyboardPhase.None);
                }

                session.Complete(working);
            }
        });
        return session;
    }

    /// <summary>Publishes the phase. "Connected" is good news only to someone who was shown a problem before.</summary>
    private void Report(Session session, KeyboardPhase phase)
    {
        if (session.RequestedAt is not null)
        {
            phase = KeyboardPhase.None; // asked for in the tray: the user is at the desktop, and the tray reports
        }

        if (phase == session.Phase || (phase is KeyboardPhase.Connected or KeyboardPhase.None && session.Phase == KeyboardPhase.None))
        {
            return;
        }

        session.Phase = phase == KeyboardPhase.Connected ? KeyboardPhase.None : phase; // after "connected" there is nothing to take back
        try
        {
            StatusChanged?.Invoke(session.Device, phase);
        }
        catch (Exception ex)
        {
            Log.Error("[Guard] Status report failed", ex);
        }
    }

    /// <summary>
    /// Knocks until the device works and keeps working for <see cref="Plan.StableFor"/>. Right after startup a keyboard
    /// may connect and drop again a few seconds later (Windows then even removes the pairing), so one "works" is not enough.
    /// </summary>
    private async Task<bool> RunSessionAsync(Session session, CancellationToken ct)
    {
        var device = session.Device;
        int knocks = 0;
        DateTime? workingSince = null;
        while (DateTime.UtcNow < session.Deadline || workingSince is not null)
        {
            ct.ThrowIfCancellationRequested();
            var state = NativeBluetooth.GetState(device.Address);
            if (session.KeyLost && state.Known && state.Paired)
            {
                session.NextKnock = DateTime.UtcNow; // pair properly while the keyboard still accepts it
            }
            else if (Types(device, state) && !(session.ForceFirstKnock && knocks == 0))
            {
                workingSince ??= DateTime.UtcNow;
                // Say so at once: someone at the sign-in screen is waiting for exactly this line.
                if (DateTime.UtcNow - workingSince >= TimeSpan.FromSeconds(1))
                {
                    Report(session, KeyboardPhase.Connected);
                }

                if (DateTime.UtcNow - workingSince >= session.Plan.StableFor)
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            if (workingSince is not null)
            {
                // It worked and dropped again. Without a pairing, pair right away; with one, give it a moment to
                // reconnect by itself first (knocking a connection that is coming up breaks it).
                workingSince = null;
                session.NextKnock = DateTime.UtcNow + (state.Known && state.Paired ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
                Log.Info($"[Guard] '{Name(device)}' stopped working again ({state}): keep restoring");
            }

            bool unpaired = !(state.Known && state.Paired);
            // Right after boot the Bluetooth stack is not up yet and every device is "unknown" for a few seconds: that is
            // not a lost pairing, so it must not start the knocking before the initial wait is over.
            if (unpaired && (state.Known || DateTime.UtcNow >= session.Started + session.Plan.InitialWait))
            {
                // A failed pairing attempt changes nothing, so try often: the keyboard accepts a new pairing only for
                // a short while after it is turned on — and then it shows up connected without a pairing: try right away.
                var soon = state.Connected ? DateTime.UtcNow : session.LastKnock + PairingInterval;
                if (soon < session.NextKnock)
                {
                    session.NextKnock = soon;
                }
            }

            if (!state.Known && DateTime.UtcNow - session.Started < TimeSpan.FromSeconds(60))
            {
                // The Bluetooth stack is still starting (right after boot): nothing can be told or done yet.
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            if (DateTime.UtcNow >= session.NextKnock)
            {
                knocks++;
                session.Report(knocks == 1 ? "Connecting…" : $"Connecting… (attempt {knocks})");
                // Force only when Windows shows it connected while it does not type: then it is reachable for sure.
                // Pairing again although the key test passes: only when the user asked for the keyboard in the tray and
                // it still does not work 20 seconds later.
                bool lastResort = session.RequestedAt is { } requested && DateTime.UtcNow - requested >= TimeSpan.FromSeconds(20);
                var outcome = await KnockAsync(device, state, knocks, force: session.ForceFirstKnock && knocks == 1 && state.Connected,
                    session.Plan.SelfConnectWait, session.RequestedAt is not null, lastResort, () => session.KeyLost,
                    phase => Report(session, phase), quiet: session.QuietKnocks % 12 != 0, ct);
                if (outcome == Knock.Paired)
                {
                    AfterPairing?.Invoke();
                }

                session.KeyLost = false;
                session.LastKnock = DateTime.UtcNow;
                switch (outcome)
                {
                    case Knock.NoAnswer:
                        Report(session, KeyboardPhase.Waiting);
                        break;
                    case Knock.Idle:
                        Report(session, KeyboardPhase.Idle);
                        break;
                    case Knock.Paired:
                        Report(session, KeyboardPhase.Connecting);
                        break;
                }
                // No answer changed nothing, so page again soon (the user may be turning the keyboard on right now);
                // after anything else give Windows time to connect.
                session.NextKnock = DateTime.UtcNow + outcome switch
                {
                    Knock.NoAnswer => session.Plan.PageInterval,
                    Knock.NotReady => TimeSpan.FromSeconds(2),
                    Knock.Idle => TimeSpan.FromSeconds(10),
                    _ => session.Plan.Interval,
                };
                // Knocks in a row that change nothing are logged once in 12; one that learned nothing does not count.
                session.QuietKnocks = outcome switch
                {
                    Knock.NoAnswer or Knock.Idle => session.QuietKnocks + 1,
                    Knock.NotReady => session.QuietKnocks,
                    _ => 0,
                };
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return Types(device, NativeBluetooth.GetState(device.Address));
    }

    /// <summary>
    /// One attempt (tested with Apple Magic Keyboard).
    /// <para>
    /// Windows has the key but the device does not work: page it first without touching the pairing (an uncached SDP
    /// query). No answer: it is asleep, off or away (or connected to another host) — keep the pairing, it reconnects by
    /// itself when it wakes. It answers: test the stored key (<see cref="NativeBluetooth.TestLinkKey"/>). Accepted: it is
    /// ours and idle, it connects when a key is pressed — keep the pairing. Rejected: another host took it — remove the
    /// old pairing and pair again at once.
    /// </para>
    /// <para>Never reinstall HID on a keyboard that may be asleep: the failed HID connection makes Windows remove a good
    /// pairing, and the keyboard refuses a new one until it is turned off and on.</para>
    /// </summary>
    private static async Task<Knock> KnockAsync(GuardedDevice device, DeviceState state, int attempt, bool force,
        TimeSpan selfConnectWait, bool requested, bool lastResort, Func<bool> keyLost, Action<KeyboardPhase> report,
        bool quiet, CancellationToken ct)
    {
        string name = Name(device);
        if (state.Known && state.Paired)
        {
            if (force)
            {
                // The tray says it does not type although Windows shows it connected: reinstall HID.
                // If the device rejects our key, Windows removes the pairing and the next attempt pairs again.
                int error = await Task.Run(() => NativeBluetooth.EnableServices(device.Address, device.Services), ct);
                Log.Info($"[Guard] '{name}' knock {attempt} ({state}): requested, services reinstalled -> {error}");
                return Knock.Other;
            }

            if (keyLost())
            {
                Log.Info($"[Guard] '{name}' knock {attempt}: it asked to pair although Windows has a key: pairing anew");
                return await PairAnewAsync(device, attempt, report, ct);
            }

            // Is it awake, and is it still ours? An awake keyboard that does not connect is either taken by another host
            // or ours and idle (nobody pressed a key yet), and for a few seconds after any connection attempt both look
            // like a working one (connected, paired, HID collections present). The stored key tells them apart.
            // The test only means something on a link it brings up itself, so let a leftover link drop first.
            string details = "";
            var test = await TestKeyAsync(device, ct);
            details = test.Details;
            if (keyLost())
            {
                // The test's authenticated connection made the keyboard ask for a new pairing (it was just turned on).
                Log.Info($"[Guard] '{name}' knock {attempt}: it asked to pair during the key test: pairing anew ({details})");
                return await PairAnewAsync(device, attempt, report, ct);
            }

            if (test.Result == LinkKeyTest.NotReady)
            {
                return Knock.NotReady; // nothing is known yet: say nothing, try again in a moment
            }

            if (test.Result == LinkKeyTest.Unreachable)
            {
                if (!quiet)
                {
                    Log.Info($"[Guard] '{name}' knock {attempt} ({state}): no answer (asleep, off or away): " +
                             $"keeping the pairing, waiting for it to wake ({details})");
                }

                return Knock.NoAnswer;
            }

            // A rejected key means another host took the keyboard. When the user asked for it in the tray, pair anew at
            // once. Otherwise only say so on the sign-in screen and keep the pairing: the keyboard has to be turned off
            // and on anyway, and when it is, the next test makes it ask to pair (see KeyLost) — proof enough to pair
            // anew. So nothing is removed on the strength of one test, and a wrong "rejected" costs nothing.
            bool takeOver = test.Result == LinkKeyTest.Rejected && requested;
            if (test.Result == LinkKeyTest.Rejected && !takeOver)
            {
                if (!quiet)
                {
                    Log.Info($"[Guard] '{name}' knock {attempt}: awake, but it rejects our key (taken by another host): " +
                             $"waiting for it to be turned off and on ({details})");
                }

                report(KeyboardPhase.PowerCycle);
                return Knock.Idle;
            }

            if (takeOver)
            {
                Log.Info($"[Guard] '{name}' knock {attempt}: awake, but it rejects our key (taken by another host): {details}");
            }
            else
            {
                // Ours (or it stopped answering): give it time to connect by itself, and make sure it stays connected.
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (waited.Elapsed < TimeSpan.FromSeconds(10))
                {
                    var now = NativeBluetooth.GetState(device.Address);
                    if (Types(device, now) || (waited.Elapsed >= selfConnectWait && !now.Connected))
                    {
                        break;
                    }

                    if (test.Result == LinkKeyTest.Accepted && waited.Elapsed >= TimeSpan.FromSeconds(3))
                    {
                        report(KeyboardPhase.Idle); // it is not connecting by itself: tell the user to press a key
                    }

                    await Task.Delay(1000, ct);
                }

                if (Types(device, NativeBluetooth.GetState(device.Address)))
                {
                    var held = System.Diagnostics.Stopwatch.StartNew();
                    bool holds = true;
                    while (holds && held.Elapsed < TimeSpan.FromSeconds(8))
                    {
                        await Task.Delay(1000, ct);
                        holds = Types(device, NativeBluetooth.GetState(device.Address));
                        if (holds && held.Elapsed >= TimeSpan.FromSeconds(2))
                        {
                            report(KeyboardPhase.Connected); // do not make the user wait for the rest of the check
                        }
                    }

                    if (holds)
                    {
                        Log.Info($"[Guard] '{name}' knock {attempt}: awake and connected");
                        return Knock.Other;
                    }
                }

                if (!lastResort)
                {
                    if (!quiet)
                    {
                        Log.Info($"[Guard] '{name}' knock {attempt}: awake but not connected, and it accepts our key — " +
                                 $"it is ours and idle (press a key): keeping the pairing ({details})");
                    }

                    return Knock.Idle;
                }

                // The user asked for the keyboard in the tray a while ago and it still does not work, although the key
                // test passes. In case the test cannot tell on this Bluetooth adapter, do what the user asked for.
                Log.Info($"[Guard] '{name}' knock {attempt}: requested in the tray and still not connected: pairing again anyway ({details})");
                takeOver = true;
            }

        }

        return await PairAnewAsync(device, attempt, report, ct);
    }

    /// <summary>
    /// Forgets the key Windows has (if any) and pairs with bonding; our callback confirms the request. Windows itself
    /// would drop a rejected key only after a failed HID connection (reinstall HID, then 15-20 s), and by then the
    /// keyboard no longer accepts a new pairing: it does so only for a short while after it is turned on.
    /// </summary>
    private static async Task<Knock> PairAnewAsync(GuardedDevice device, int attempt, Action<KeyboardPhase> report, CancellationToken ct)
    {
        string name = Name(device);
        var state = NativeBluetooth.GetState(device.Address);
        if ((state.Known && state.Paired) || state.Connected)
        {
            // The first attempt, or the keyboard is here right now. Repeated attempts while it has to be turned off
            // and on say nothing: the status must not flicker between "pairing" and "turn it off and on".
            report(KeyboardPhase.Pairing);
        }

        if (state.Known && state.Paired)
        {
            int removeError = await Task.Run(() => NativeBluetooth.RemovePairing(device.Address), ct);
            Log.Info($"[Guard] '{name}' knock {attempt}: old pairing removed -> {removeError}");
            state = NativeBluetooth.GetState(device.Address);
        }

        int pairError = await Task.Run(() => NativeBluetooth.PairWithBonding(device.Address), ct);
        Log.Info($"[Guard] '{name}' knock {attempt} ({state}): pairing -> {pairError}" + pairError switch
        {
            NativeBluetooth.ErrorTimeout => " (no answer: the device is asleep, off or busy with another host)",
            NativeBluetooth.ErrorNotAuthenticated => " (refused: the device accepts a new pairing only for a while after it is turned on)",
            _ => "",
        });
        if (pairError == NativeBluetooth.ErrorSuccess)
        {
            // Pairing stores the key but does not install the HID service; without it the keyboard cannot connect.
            await Task.Delay(1000, ct);
            int error = NativeBluetooth.EnableServices(device.Address, device.Services);
            Log.Info($"[Guard] '{name}' services enabled after pairing -> {error}: {NativeBluetooth.GetState(device.Address)}");
            return Knock.Paired;
        }

        report(KeyboardPhase.PowerCycle); // it accepts a new pairing only shortly after it is turned on
        return Knock.Other;
    }

    private enum Knock
    {
        /// <summary>The device was paired again.</summary>
        Paired,
        /// <summary>Windows has the key, but the device did not answer a page: nothing was changed.</summary>
        NoAnswer,
        /// <summary>It answered a page but did not stay connected, and it was not taken over: nothing was changed.</summary>
        Idle,
        /// <summary>Bluetooth is still starting, or an earlier link is still up: the key could not be tested.</summary>
        NotReady,
        Other,
    }

    /// <summary>When a key test last left a link to the device up.</summary>
    private static readonly ConcurrentDictionary<ulong, DateTime> LastLink = new();

    /// <summary>How long after a link was left up the next key test means something.</summary>
    private static readonly TimeSpan LinkRest = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Tests the stored link key (<see cref="NativeBluetooth.TestLinkKey"/>) on a fresh link. For several seconds after
    /// any connection — even when Windows already reports the device as not connected — the next one reuses the link,
    /// Windows does not authenticate again, and a rejected key looks accepted (seen twice: "rejected", and a second
    /// later "accepted"). So wait until the link has had time to drop; if it cannot, the test is put off.
    /// </summary>
    private static async Task<(LinkKeyTest Result, string Details)> TestKeyAsync(GuardedDevice device, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (NativeBluetooth.GetState(device.Address).Connected
               || (LastLink.TryGetValue(device.Address, out var last) && DateTime.UtcNow - last < LinkRest))
        {
            if (DateTime.UtcNow >= deadline || Types(device, NativeBluetooth.GetState(device.Address)))
            {
                // Still up — or the keyboard has connected meanwhile (a key was pressed): back to the session, which
                // notices that at once.
                return (LinkKeyTest.NotReady, "an earlier link is still up");
            }

            await Task.Delay(500, ct);
        }

        string details = "";
        var result = await Task.Run(() => NativeBluetooth.TestLinkKey(device.Address, out details), ct);
        if (result is LinkKeyTest.Accepted or LinkKeyTest.Rejected)
        {
            LastLink[device.Address] = DateTime.UtcNow; // both leave a link up for a few seconds
        }

        return (result, details);
    }

    /// <summary>
    /// Pages the device with an uncached SDP query, which needs no pairing and does not change it.
    /// true: it answered; false: no answer; null: could not tell.
    /// </summary>
    private static async Task<bool?> PageAsync(ulong address, CancellationToken ct)
    {
        try
        {
            using var device = await Windows.Devices.Bluetooth.BluetoothDevice.FromBluetoothAddressAsync(address).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (device is null)
            {
                return null;
            }

            var result = await device.GetRfcommServicesAsync(Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached)
                .AsTask(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            return result.Error == Windows.Devices.Bluetooth.BluetoothError.Success;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info($"[Guard] Page failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Called on the message loop thread when a guarded device asks to pair.</summary>
    private void OnPairingRequest(NativeBluetooth.PairingRequest request)
    {
        // A request for a device Windows still has a key for: the device no longer has that key (another host took it,
        // and it has just been turned on), and the authenticated connection of the key test made Windows pair anew.
        // Such a pairing works for the moment, but the session then pairs properly (see KnockAsync).
        bool staleKey = NativeBluetooth.GetState(request.Address) is { Known: true, Paired: true };
        bool accept;
        lock (_sync)
        {
            accept = _sessions.TryGetValue(request.Address, out var session);
            if (accept && staleKey)
            {
                session!.KeyLost = true;
            }
        }

        int error = NativeBluetooth.AnswerPairingRequest(request, accept);
        Log.Info($"[Guard] Pairing request from {BluetoothAddress.Format(request.Address)} '{request.Name}': {request.Method}, " +
                 $"{request.Requirements} -> {(accept ? "accepted" : "rejected (no session)")}, error {error}" +
                 (accept && request.WithoutBonding ? " (without bonding: the key is not kept, the next knock pairs with bonding)" : "") +
                 (accept && staleKey ? " (Windows still had a key for it: the device lost that key, pairing anew)" : ""));
        if (accept && error == 0 && !request.WithoutBonding)
        {
            AfterPairing?.Invoke();
        }
    }

    private void AddRegistration(ulong address)
    {
        if (_registrations.TryGetValue(address, out var entry))
        {
            _registrations[address] = (entry.Registration, entry.Count + 1);
            return;
        }

        var registration = NativeBluetooth.RegisterForPairing(address, OnPairingRequest);
        _registrations[address] = (registration, 1);
    }

    private void RemoveRegistration(ulong address)
    {
        if (!_registrations.TryGetValue(address, out var entry))
        {
            return;
        }

        if (entry.Count > 1)
        {
            _registrations[address] = (entry.Registration, entry.Count - 1);
            return;
        }

        entry.Registration.Dispose();
        _registrations.Remove(address);
    }

    private Task RunOnLoopAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loopActions.Enqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        PostThreadMessage(_loopThreadId, WmApp, IntPtr.Zero, IntPtr.Zero);
        return tcs.Task;
    }

    private void RunLoop()
    {
        _loopThreadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // creates the thread's message queue
        _loopReady.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.hwnd == IntPtr.Zero && msg.message == WmApp)
            {
                while (_loopActions.TryDequeue(out var action))
                {
                    action();
                }

                continue;
            }

            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        foreach (var (registration, _) in _registrations.Values)
        {
            registration.Dispose();
        }

        _registrations.Clear();
    }

    public void Dispose()
    {
        _stateTimer.Dispose();
        _stopping.Cancel();
        PostThreadMessage(_loopThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _loop.Join(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Writes every change of a guarded device's state to the log (checked every 5 seconds), so it is clear later
    /// what happened between events, e.g. on the sign-in screen.
    /// </summary>
    private void LogStateChanges()
    {
        if (Interlocked.Exchange(ref _loggingStates, 1) == 1)
        {
            return;
        }

        try
        {
            foreach (var device in Devices)
            {
                var state = NativeBluetooth.GetState(device.Address);
                string text = state.ToString();
                if (_lastStates.TryGetValue(device.Address, out var last) && last == text)
                {
                    continue;
                }

                _lastStates[device.Address] = text;
                Log.Info($"[Guard] '{Name(device)}' state: {text}, {DescribeHid(device, state)}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("[Guard] State check failed", ex);
        }
        finally
        {
            Volatile.Write(ref _loggingStates, 0);
        }
    }

    /// <summary>How many HID collections Windows has for the device: none while it is connected means it does not type.</summary>
    private static string DescribeHid(GuardedDevice device, DeviceState state)
    {
        if (!state.Connected)
        {
            return "HID -";
        }

        try
        {
            return CountHid(device) is { } count ? $"HID collections {count}" : "HID ? (no container)";
        }
        catch (Exception ex)
        {
            return $"HID ? ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// The device can type: connected, paired and with HID collections. "Connected" alone is not enough: a page (SDP
    /// query) briefly connects the device too, and a keyboard whose HID does not start stays connected for a few
    /// seconds without typing. Blocking. If the HID collections cannot be counted, the connection state decides.
    /// </summary>
    private static bool Types(GuardedDevice device, DeviceState state)
    {
        if (!state.IsWorking)
        {
            return false;
        }

        try
        {
            return CountHid(device) is not 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Number of HID collections of the device; null if its PnP container is not found. Blocking.</summary>
    public static int? CountHid(GuardedDevice device)
    {
        var saved = new SavedDevice { Address = device.Address, Name = device.Name };
        var containerId = BatteryReader.TryGetContainerIdAsync(saved).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        return containerId is { } id
            ? HidNative.FindCollectionPathsAsync(id).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult().Count
            : null;
    }

    private static string Name(GuardedDevice device) =>
        string.IsNullOrWhiteSpace(device.Name) ? BluetoothAddress.Format(device.Address) : device.Name;

    /// <summary>How long to wait before the first knock, how often to knock and when to give up.</summary>
    /// <summary>
    /// How long to wait before the first knock, how often to knock, when to give up,
    /// and how long the device must keep working before the session ends.
    /// </summary>
    private sealed record Plan(TimeSpan InitialWait, TimeSpan Interval, TimeSpan Duration, TimeSpan StableFor)
    {
        /// <summary>
        /// How soon to page again a device that did not answer. Short for every plan: a page changes nothing, and a
        /// keyboard that another host took accepts a new pairing only for a short while after it is turned on, so it
        /// has to be noticed within seconds — also at the sign-in screen.
        /// </summary>
        public TimeSpan PageInterval { get; init; } = TimeSpan.FromSeconds(3);

        /// <summary>The user asked for the keyboard in the tray.</summary>
        public bool Requested { get; init; }

        /// <summary>How long a device that answered a page gets to connect by itself.</summary>
        public TimeSpan SelfConnectWait { get; init; } = TimeSpan.FromSeconds(10);

        public static Plan For(ServiceTrigger trigger) => trigger switch
        {
            // After boot or wake the keyboard reconnects by itself within seconds: give it a moment. The wait used to be
            // 45 s, when a knock could cost a good pairing; the key test does not touch the pairing, and a keyboard that
            // another host took must be noticed soon after it is turned on.
            ServiceTrigger.Startup or ServiceTrigger.Resume =>
                new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(60)),
            _ => new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60)),
        };

        /// <summary>The tray waits for the answer, so it only needs a short confirmation.</summary>
        public static Plan Request(TimeSpan duration) => new(TimeSpan.Zero, TimeSpan.FromSeconds(10), duration, TimeSpan.FromSeconds(10))
        {
            // The user is waiting and may be turning the keyboard off and on right now: notice it within seconds.
            Requested = true,
            PageInterval = TimeSpan.FromSeconds(2),
            SelfConnectWait = TimeSpan.FromSeconds(5),
        };
    }

    private sealed class Session(GuardedDevice device, Plan plan, Action<string>? progress)
    {
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<string>? _progress = progress;

        public GuardedDevice Device { get; } = device;
        public Plan Plan { get; private set; } = plan;
        public DateTime Deadline { get; private set; } = DateTime.UtcNow + plan.Duration;
        public DateTime NextKnock { get; set; } = DateTime.UtcNow + plan.InitialWait;

        public DateTime Started { get; } = DateTime.UtcNow;

        /// <summary>The phase shown in the status window; None: nothing was shown (or it has been taken back).</summary>
        public KeyboardPhase Phase { get; set; }

        /// <summary>The device asked to pair although Windows has a key for it: that key is useless.</summary>
        public volatile bool KeyLost;

        /// <summary>When the user asked for the device in the tray; null for a session started by an event.</summary>
        public DateTime? RequestedAt { get; private set; } = plan.Requested ? DateTime.UtcNow : null;

        /// <summary>Knocks in a row that changed nothing: only every 12th is logged.</summary>
        public int QuietKnocks { get; set; }

        public DateTime LastKnock { get; set; } = DateTime.MinValue;

        /// <summary>Knock once even if Windows reports the device as working (the tray says it does not type).</summary>
        public bool ForceFirstKnock { get; set; }
        public Task<bool> Completion => _completion.Task;

        /// <summary>A request from the tray joins the session: knock now and keep going for at least <paramref name="duration"/>.</summary>
        public void Expedite(TimeSpan duration, Action<string>? progress)
        {
            Plan = Plan with
            {
                Interval = TimeSpan.FromSeconds(10),
                StableFor = TimeSpan.FromSeconds(10),
                Requested = true,
                PageInterval = TimeSpan.FromSeconds(2),
                SelfConnectWait = TimeSpan.FromSeconds(5),
            };
            NextKnock = DateTime.UtcNow;
            RequestedAt = DateTime.UtcNow;
            var deadline = DateTime.UtcNow + duration;
            if (deadline > Deadline)
            {
                Deadline = deadline;
            }

            _progress = progress ?? _progress;
        }

        public void Report(string text) => _progress?.Invoke(text);

        public void Complete(bool working) => _completion.TrySetResult(working);
    }

    #region P/Invoke (message loop)

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    #endregion
}
