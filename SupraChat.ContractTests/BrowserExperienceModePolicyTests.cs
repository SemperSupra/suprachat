using SupraChat.Core;

namespace SupraChat.ContractTests;

public static class BrowserExperienceModePolicyTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static readonly Dictionary<string, (string Chat, string Work)> Labels = new()
    {
        ["en"] = ("Chat", "Work"),
        ["de"] = ("Chat", "Arbeit"),
        ["th"] = ("\u0e41\u0e0a\u0e17", "\u0e07\u0e32\u0e19")
    };

    private static BrowserExperienceModePolicy.CandidatePayload MakeFixture(
        string locale = "en",
        string selection = "CHAT",
        string session = "a",
        long epoch = 1)
    {
        var (a, b) = Labels[locale];
        return new BrowserExperienceModePolicy.CandidatePayload(
            Source: "SYNTHETIC_TYPED_ADAPTER",
            Origin: "https://chatgpt.com",
            Session: session,
            Epoch: epoch,
            Timestamp: 5000,
            Locale: locale,
            Controls: new[]
            {
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", a, true, selection == "CHAT"),
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", b, true, selection == "WORK")
            });
    }

    private static BrowserExperienceModePolicy.PreflightDecision Check(
        BrowserExperienceModePolicy.CandidatePayload? candidate,
        string intent = "CHAT",
        string session = "a",
        long epoch = 1,
        long now = 5100,
        BrowserExperienceModePolicy.HostWorkLease? lease = null)
    {
        return BrowserExperienceModePolicy.Preflight(new BrowserExperienceModePolicy.PreflightRequest(
            Intent: intent,
            Candidate: candidate,
            Session: session,
            Epoch: epoch,
            Now: now,
            Lease: lease));
    }

    public static void Run()
    {
        // 1. Locale testing for en, de, th
        foreach (var locale in Labels.Keys)
        {
            var chatFixture = MakeFixture(locale);
            Require(BrowserExperienceModePolicy.Observe(chatFixture) == "CHAT", $"Observe must return CHAT for locale {locale}");

            var workFixture = MakeFixture(locale, "WORK");
            Require(BrowserExperienceModePolicy.Observe(workFixture) == "WORK", $"Observe must return WORK for locale {locale}");

            var preflightChat = Check(chatFixture);
            Require(preflightChat.Admission == "HOST_REVIEW_ONLY", $"Preflight must return HOST_REVIEW_ONLY for {locale}");
            Require(preflightChat.Reason == "SYNTHETIC_OBSERVATION", $"Preflight reason must be SYNTHETIC_OBSERVATION for {locale}");
            Require(preflightChat.Effect == "NONE", $"Preflight effect must be NONE for {locale}");
        }

        // 2. Missing, duplicate, or spoofed switches
        var emptyControls = MakeFixture() with { Controls = Array.Empty<BrowserExperienceModePolicy.ControlElement>() };
        Require(BrowserExperienceModePolicy.Observe(emptyControls) == "UNKNOWN", "Empty controls must observe UNKNOWN");

        var duplicateControls = MakeFixture() with
        {
            Controls = new[]
            {
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", "Chat", true, true),
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", "Work", true, false),
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", "Chat", true, true)
            }
        };
        Require(BrowserExperienceModePolicy.Observe(duplicateControls) == "CONFLICT", "Duplicate switch controls must observe CONFLICT");

        var bothSelected = MakeFixture() with
        {
            Controls = new[]
            {
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", "Chat", true, true),
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", "Work", true, true)
            }
        };
        Require(BrowserExperienceModePolicy.Observe(bothSelected) == "CONFLICT", "Both controls selected must observe CONFLICT");

        var neitherSelected = MakeFixture() with
        {
            Controls = new[]
            {
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", "Chat", true, false),
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", "Work", true, false)
            }
        };
        Require(BrowserExperienceModePolicy.Observe(neitherSelected) == "UNKNOWN", "Neither control selected must observe UNKNOWN");

        var hiddenControl = MakeFixture() with
        {
            Controls = new[]
            {
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", "Chat", false, true),
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", "Work", true, false)
            }
        };
        Require(BrowserExperienceModePolicy.Observe(hiddenControl) == "UNKNOWN", "Hidden control must observe UNKNOWN");

        var spoofedOutsideScope = MakeFixture() with
        {
            Controls = new[]
            {
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", "Chat", true, true),
                new BrowserExperienceModePolicy.ControlElement("experience-switch", "tab", "Work", true, false),
                new BrowserExperienceModePolicy.ControlElement("assistant-message", "tab", "Work", true, true)
            }
        };
        Require(BrowserExperienceModePolicy.Observe(spoofedOutsideScope) == "CHAT", "Controls outside experience-switch scope must be ignored");

        // 3. Work mode requires explicit bounded host operator lease
        var workCandidate = MakeFixture("en", "WORK");
        var workWithoutLease = Check(workCandidate, "WORK");
        Require(workWithoutLease.Admission == "REFUSED", "Work without lease must be REFUSED");
        Require(workWithoutLease.Reason == "WORK_APPROVAL_REQUIRED", "Work without lease reason mismatch");

        var validLease = new BrowserExperienceModePolicy.HostWorkLease(
            Type: "EXPLICIT_HOST_WORK_LEASE",
            Session: "a",
            Epoch: 1,
            Confirmed: true,
            Expires: 7000);

        var workWithLease = Check(workCandidate, "WORK", lease: validLease);
        Require(workWithLease.Admission == "HOST_REVIEW_ONLY", "Work with valid lease must be admitted for review");

        // Negative lease checks
        Require(Check(workCandidate, "WORK", lease: validLease with { Confirmed = false }).Admission == "REFUSED", "Unconfirmed lease must be REFUSED");
        Require(Check(workCandidate, "WORK", lease: validLease with { Epoch = 2 }).Admission == "REFUSED", "Epoch mismatched lease must be REFUSED");
        Require(Check(workCandidate, "WORK", lease: validLease with { Expires = 5100 }).Admission == "REFUSED", "Expired lease must be REFUSED");
        Require(Check(workCandidate, "WORK", lease: validLease with { Type = "OTHER" }).Admission == "REFUSED", "Wrong lease type must be REFUSED");

        // 4. Navigation races, session mismatch, staleness, and intent mismatch
        var defaultCandidate = MakeFixture();
        Require(Check(defaultCandidate, "CHAT", epoch: 2).Admission == "REFUSED", "Epoch mismatch must be REFUSED");
        Require(Check(defaultCandidate, "CHAT", session: "b").Admission == "REFUSED", "Session mismatch must be REFUSED");
        Require(Check(defaultCandidate, "CHAT", now: 7100).Admission == "REFUSED", "Stale candidate (>2000ms) must be REFUSED");
        Require(Check(defaultCandidate, "WORK").Admission == "REFUSED", "Intent mismatch must be REFUSED");

        // 5. 3-audience projection
        var decision = Check(defaultCandidate);
        Require(decision.Effect == "NONE", "Decision effect must be NONE");
        foreach (var audience in new[] { "human", "automation", "agent" })
        {
            var projected = BrowserExperienceModePolicy.ForAudience(decision, audience);
            Require(projected.Schema == BrowserExperienceModePolicy.Schema, "Audience schema mismatch");
            Require(projected.Evidence == "SYNTHETIC_ONLY", "Evidence must be SYNTHETIC_ONLY");
            Require(projected.Adapter == "NOT_CONNECTED", "Adapter must be NOT_CONNECTED");
            Require(projected.Effect == "NONE", "Audience effect must be NONE");
            Require(projected.Audience == audience, "Audience mismatch");
        }

        // 6. 256 synthetic perturbations preserving refusal on unqualified state
        for (int i = 0; i < 256; i++)
        {
            var basePayload = new BrowserExperienceModePolicy.CandidatePayload(
                Source: "SYNTHETIC_TYPED_ADAPTER",
                Origin: "https://chatgpt.com",
                Session: "s",
                Epoch: 3,
                Timestamp: 1000,
                Locale: "en",
                Controls: new List<BrowserExperienceModePolicy.ControlElement>
                {
                    new("experience-switch", "tab", "Chat", true, true),
                    new("experience-switch", "tab", "Work", true, false)
                });

            var change = i % 8;
            var perturbedControls = basePayload.Controls!.ToList();
            var perturbedOrigin = basePayload.Origin;
            var perturbedSession = basePayload.Session;
            var perturbedEpoch = basePayload.Epoch;

            switch (change)
            {
                case 0:
                    perturbedOrigin = "https://other.invalid";
                    break;
                case 1:
                    perturbedSession = "other";
                    break;
                case 2:
                    perturbedEpoch = 9;
                    break;
                case 3:
                    perturbedControls[0] = perturbedControls[0] with { Visible = false };
                    break;
                case 4:
                    perturbedControls[0] = perturbedControls[0] with { Selected = false };
                    break;
                case 5:
                    perturbedControls[1] = perturbedControls[1] with { Selected = true };
                    break;
                case 6:
                    perturbedControls.Add(perturbedControls[0]);
                    break;
                case 7:
                    perturbedControls[1] = perturbedControls[1] with { Role = "paragraph" };
                    break;
            }

            var perturbedCandidate = basePayload with
            {
                Origin = perturbedOrigin,
                Session = perturbedSession,
                Epoch = perturbedEpoch,
                Controls = perturbedControls
            };

            var pertDecision = BrowserExperienceModePolicy.Preflight(new BrowserExperienceModePolicy.PreflightRequest(
                Intent: "CHAT",
                Candidate: perturbedCandidate,
                Session: "s",
                Epoch: 3,
                Now: 1100));

            Require(pertDecision.Admission == "REFUSED", $"Perturbation {i} (variant {change}) must be REFUSED");
            Require(pertDecision.Effect == "NONE", $"Perturbation {i} effect must be NONE");
        }
    }
}
