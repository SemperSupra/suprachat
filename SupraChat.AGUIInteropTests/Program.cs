using System.Text.Json;
using AGUI.Abstractions;

static class Program
{
    private static int _checks;
    private static void Check(bool condition, string code)
    {
        if (!condition) throw new InvalidOperationException("R47_AGUI_FAIL_"+code);
        _checks++;
    }
    public static int Main()
    {
        // Official AG-UI .NET package is the source of all wire classes:
        // no copied proto structs or maintained protocol fork.
        var started = new RunStartedEvent
        {
            ThreadId="same-session-id",
            RunId="run-1",
            ProtocolVersion="1.0"
        };
        var startJson=JsonSerializer.Serialize(started);
        using var startDoc=JsonDocument.Parse(startJson);
        Check(startDoc.RootElement.GetProperty("type").GetString()=="RUN_STARTED","run_type");
        Check(startDoc.RootElement.GetProperty("threadId").GetString()=="same-session-id","canonical_thread");
        Check(startDoc.RootElement.GetProperty("runId").GetString()=="run-1","run_identity");
        Check(startDoc.RootElement.GetProperty("protocolVersion").GetString()=="1.0","version");

        var update=new StateDeltaEvent
        {
            Delta=JsonSerializer.SerializeToElement(new []{
                new {op="replace",path="/control/epoch",value=2}
            })
        };
        var json=JsonSerializer.Serialize(update);
        using var doc=JsonDocument.Parse(json);
        Check(doc.RootElement.GetProperty("type").GetString()=="STATE_DELTA","state_delta_type");
        var delta=doc.RootElement.GetProperty("delta");
        Check(delta.ValueKind==JsonValueKind.Array && delta.GetArrayLength()==1,"delta_array");
        Check(delta[0].GetProperty("path").GetString()=="/control/epoch","epoch_projection");
        Check(!json.Contains("\"approved\":true",StringComparison.OrdinalIgnoreCase),"no_self_approval");

        var interrupt=new InterruptRequestContent("human-control-request-1")
        {
            Reason="input_required",
            Message="Review requested; external human authentication required",
            Metadata=JsonSerializer.SerializeToElement(new {
                controlEpoch=2, sessionId="same-session-id",
                claimedHumanIdentityVerified=false, toolExecutionGranted=false
            })
        };
        var interruptJson=JsonSerializer.Serialize(interrupt);
        using var interruptDoc=JsonDocument.Parse(interruptJson);
        Check(interruptDoc.RootElement.GetProperty("reason").GetString()=="input_required","interrupt_reason");
        Check(interruptDoc.RootElement.GetProperty("metadata")
            .GetProperty("claimedHumanIdentityVerified").GetBoolean()==false,"no_identity_from_wire");
        Check(interruptDoc.RootElement.GetProperty("metadata")
            .GetProperty("toolExecutionGranted").GetBoolean()==false,"no_tool_authority");
        Check(!interruptJson.Contains("refresh_token",StringComparison.OrdinalIgnoreCase),"no_creds");

        // A user-facing resume/approve payload is only an envelope. The
        // SupraChat trusted host must still independently validate identity,
        // session/epoch, tool effect, revocation and durable first outcome.
        var response=new InterruptResponseContent("human-control-request-1")
        {
            Payload=JsonSerializer.SerializeToElement(new {
                command="pause", externalAdmissionRequired=true
            })
        };
        var responseJson=JsonSerializer.Serialize(response);
        using var responseDoc=JsonDocument.Parse(responseJson);
        Check(responseDoc.RootElement.GetProperty("payload")
            .GetProperty("command").GetString()=="pause","roundtrip_response_shape");
        Check(responseDoc.RootElement.GetProperty("payload")
            .GetProperty("externalAdmissionRequired").GetBoolean(),"not_a_grant");

        Console.WriteLine($"{{\"schema\":\"suprachat-r47-agui-interop/v1\",\"result\":\"PASS\",\"checks\":{_checks},"+
            "\"package\":\"AGUI.Abstractions\",\"version\":\"1.0.1\",\"source_only\":true,"+
            "\"real_human_authenticated\":false,\"tool_effect_executed\":false,\"production_admitted\":false}");
        return 0;
    }
}
