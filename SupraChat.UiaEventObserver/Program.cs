using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.EventHandlers;
using FlaUI.Core.Identifiers;
using FlaUI.UIA3;
using FlaUI.UIA3.Identifiers;
using FlaUI.UIA3.Patterns;

namespace SupraChat.UiaEventObserver;

internal static class Program
{
    // Registration and removal occur on this same MTA control thread.
    [MTAThread]
    public static int Main(string[] args)
    {
        PropertyChangedEventHandlerBase? propertyHandler = null;
        AutomationEventHandlerBase? textHandler = null;
        UIA3Automation? automation = null;
        var removed = false;
        try
        {
            if (!OperatingSystem.IsWindows() || args.Length is not (2 or 3) ||
                (args.Length == 3 && args[2] != "--negative-no-property-listener") || !int.TryParse(args[0], out var pid) ||
                !long.TryParse(args[1], out var hwnd) || pid <= 0 || hwnd <= 0) throw new InvalidOperationException();
            var synthetic = Environment.GetEnvironmentVariable("SUPRACHAT_SYNTHETIC_UIA_SECRET");
            if (synthetic is null || synthetic.Length is < 24 or > 80 ||
                !synthetic.StartsWith("SyntheticOnly", StringComparison.Ordinal)) throw new InvalidOperationException();
            using var process = Process.GetProcessById(pid);
            process.Refresh();
            if (process.HasExited || process.MainWindowHandle.ToInt64() != hwnd) throw new InvalidOperationException();
            automation = new UIA3Automation();
            var root = automation.FromHandle(new IntPtr(hwnd));
            if (root.Properties.ProcessId.Value != pid || root.AutomationId != "SupraChat.SyntheticSecurityProbe")
                throw new InvalidOperationException();
            AutomationElement Find(string id)
            {
                var found = root.FindAllDescendants(cf => cf.ByAutomationId(id));
                if (found.Length != 1 || found[0].Properties.ProcessId.Value != pid) throw new InvalidOperationException();
                return found[0];
            }
            _ = Find("Synthetic.Probe.Passphrase");
            var state = Find("Synthetic.Probe.State");
            var eventValue = Find("Synthetic.Probe.EventValue");
            var properties = new[] { AutomationObjectIds.NameProperty, AutomationObjectIds.HelpTextProperty,
                ValuePattern.ValueProperty, LegacyIAccessiblePattern.NameProperty, LegacyIAccessiblePattern.ValueProperty,
                LegacyIAccessiblePattern.DescriptionProperty, LegacyIAccessiblePattern.HelpProperty,
                LegacyIAccessiblePattern.DefaultActionProperty };
            var counts = new int[properties.Length];
            var armed = 0; var filled = 0; var disclosure = 0; var unknown = 0;
            var valueArmed = 0; var valueFilled = 0;
            var callbacks = 0; var textCallbacks = 0; var removalPhase = 0; var freshAfterRemoval = 0;
            if (args.Length == 2) propertyHandler = root.RegisterPropertyChangedEvent(TreeScope.Subtree, (sender, property, value) =>
            {
                try
                {
                    if (Interlocked.Increment(ref callbacks) > 256) { Interlocked.Exchange(ref unknown, 1); return; }
                    if (sender.Properties.ProcessId.Value != pid) { Interlocked.Exchange(ref unknown, 1); return; }
                    var id = sender.AutomationId;
                    if (id == "Synthetic.Probe.EventValue" && property.Id == ValuePattern.ValueProperty.Id && value is string publicValue)
                    {
                        if (publicValue == "ARMED") Interlocked.Exchange(ref valueArmed, 1);
                        if (publicValue == "FILLED") Interlocked.Exchange(ref valueFilled, 1);
                        if (publicValue == "POST_REMOVAL" && Volatile.Read(ref removalPhase) != 0)
                            Interlocked.Exchange(ref freshAfterRemoval, 1);
                    }
                    if (id == "Synthetic.Probe.State" && property.Id == AutomationObjectIds.NameProperty.Id && value is string control)
                    {
                        if (control == "ARMED") Interlocked.Exchange(ref armed, 1);
                        if (control == "FILLED") Interlocked.Exchange(ref filled, 1);
                        if (control == "POST_REMOVAL" && Volatile.Read(ref removalPhase) != 0)
                            Interlocked.Exchange(ref freshAfterRemoval, 1);
                    }
                    if (id != "Synthetic.Probe.Passphrase") return;
                    var index = Array.FindIndex(properties, p => p.Id == property.Id);
                    if (index < 0) { Interlocked.Exchange(ref unknown, 1); return; }
                    Interlocked.Increment(ref counts[index]);
                    // Raw newValue never leaves this callback or enters a trace.
                    if (value is string text)
                    {
                        if (text.Contains(synthetic, StringComparison.Ordinal)) Interlocked.Exchange(ref disclosure, 1);
                    }
                    else if (value is not null && value is not int && value is not bool)
                        Interlocked.Exchange(ref unknown, 1);
                }
                catch { Interlocked.Exchange(ref unknown, 1); }
            }, properties);
            textHandler = root.RegisterAutomationEvent(TextPattern.TextChangedEvent, TreeScope.Subtree, (sender, _) =>
            {
                try
                {
                    if (sender.Properties.ProcessId.Value != pid) { Interlocked.Exchange(ref unknown, 1); return; }
                    if (sender.AutomationId == "Synthetic.Probe.Passphrase" && Interlocked.Increment(ref textCallbacks) > 256)
                        Interlocked.Exchange(ref unknown, 1);
                }
                catch { Interlocked.Exchange(ref unknown, 1); }
            });
            Console.WriteLine("LISTENER_READY");
            Read("CONTROL");
            Wait(() => Volatile.Read(ref armed) == 1 && Volatile.Read(ref valueArmed) == 1);
            Console.WriteLine("CONTROL_EVENT_OBSERVED");
            Read("FILLED");
            Wait(() => Volatile.Read(ref filled) == 1 && Volatile.Read(ref valueFilled) == 1);
            Thread.Sleep(300); // Bounded delivery drain, not proof from silence alone.
            var propertyKeepAlive = propertyHandler; propertyHandler = null;
            var textKeepAlive = textHandler; textHandler = null;
            propertyKeepAlive!.Dispose();
            textKeepAlive.Dispose();
            removed = true; Interlocked.Exchange(ref removalPhase, 1);
            Console.WriteLine("LISTENERS_REMOVED");
            Read("POST_REMOVAL");
            if (state.Properties.Name.Value != "POST_REMOVAL" || eventValue.Patterns.Value.Pattern.Value.Value != "POST_REMOVAL")
                throw new InvalidOperationException();
            Thread.Sleep(750);
            GC.KeepAlive(propertyKeepAlive); GC.KeepAlive(textKeepAlive);
            automation.Dispose(); automation = null;
            var passed = Volatile.Read(ref armed) == 1 && Volatile.Read(ref filled) == 1 &&
                Volatile.Read(ref disclosure) == 0 && Volatile.Read(ref unknown) == 0 && Volatile.Read(ref freshAfterRemoval) == 0;
            Console.WriteLine(JsonSerializer.Serialize(new {
                schema="suprachat-synthetic-uia-property-event-witness/v1", scope="SYNTHETIC_PROPERTY_CHANGED_VALUES_ONLY",
                passed, listener_ready=true, positive_control_observed=armed == 1, fill_control_observed=filled == 1,
                value_control_observed=valueArmed == 1, value_fill_observed=valueFilled == 1,
                legacy_event_source_independently_qualified=false,
                sensitive_value_disclosed=disclosure != 0, callback_unknown=unknown != 0,
                sensitive_property_counts=properties.Select((p,i) => new {property_id=p.Id,count=Volatile.Read(ref counts[i])}).ToArray(),
                text_changed_callbacks=textCallbacks, text_event_content_read=false, text_event_content_qualified=false,
                removal_returned=removed, post_removal_mutation_verified=true, fresh_post_removal_delivery=freshAfterRemoval != 0,
                cleanup_window_ms=750, raw_values_exported=false, real_credentials_used=false, production_confidentiality_qualified=false
            }));
            return passed ? 0 : 2;
        }
        catch
        {
            Console.WriteLine("EVENT_WITNESS_NOT_VERIFIED");
            return 2;
        }
        finally
        {
            // Keep callbacks strongly reachable through the removal observation.
            try { propertyHandler?.Dispose(); } catch { }
            try { textHandler?.Dispose(); } catch { }
            try { automation?.Dispose(); } catch { }
        }
    }

    private static void Read(string expected)
    {
        if (Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult() != expected)
            throw new InvalidOperationException();
    }
    private static void Wait(Func<bool> observed)
    {
        var elapsed = Stopwatch.StartNew();
        while (!observed() && elapsed.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(20);
        if (!observed()) throw new InvalidOperationException();
    }
}
