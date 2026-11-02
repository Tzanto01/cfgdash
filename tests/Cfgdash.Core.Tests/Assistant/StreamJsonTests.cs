using Cfgdash.Core.Assistant;

namespace Cfgdash.Core.Tests.Assistant;

public sealed class StreamJsonTests
{
    [Fact]
    public void The_system_init_line_is_an_init_event_carrying_the_object()
    {
        var parsed = StreamJson.Parse("""{"type":"system","subtype":"init","tools":["a"]}""");

        var init = Assert.IsType<StreamEvent.Init>(parsed);
        Assert.Equal("a", (string?)init.Event["tools"]![0]);
    }

    [Fact]
    public void A_text_delta_stream_event_yields_the_text()
    {
        var parsed = StreamJson.Parse("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}}""");

        Assert.Equal(new StreamEvent.TextDelta("Hel"), parsed);
    }

    [Theory]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"{"}}}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"message_start"}}""")]
    [InlineData("""{"type":"assistant","message":{}}""")]
    [InlineData("""{"type":"system","subtype":"status"}""")]
    [InlineData("""{"no":"type"}""")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("""{"type":"result",""")]
    [InlineData("")]
    public void Anything_else_including_malformed_json_is_other(string line)
    {
        Assert.IsType<StreamEvent.Other>(StreamJson.Parse(line));
    }

    [Fact]
    public void A_successful_result_carries_cost_subtype_and_text()
    {
        var parsed = StreamJson.Parse("""{"type":"result","subtype":"success","is_error":false,"total_cost_usd":0.0123,"result":"done"}""");

        Assert.Equal(new StreamEvent.Result(0.0123, false, "success", "done"), parsed);
    }

    [Fact]
    public void A_result_flagged_as_error_is_an_error()
    {
        var parsed = Assert.IsType<StreamEvent.Result>(StreamJson.Parse("""{"type":"result","subtype":"error_max_budget_usd","is_error":true}"""));

        Assert.True(parsed.IsError);
        Assert.Null(parsed.CostUsd);
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"success"}""")]
    [InlineData("""{"type":"result","is_error":"false"}""")]
    [InlineData("""{"type":"result","is_error":null}""")]
    public void A_result_without_a_boolean_false_is_error_counts_as_an_error(string line)
    {
        Assert.True(Assert.IsType<StreamEvent.Result>(StreamJson.Parse(line)).IsError);
    }

    [Fact]
    public void A_control_request_is_recognized()
    {
        Assert.IsType<StreamEvent.ControlRequest>(StreamJson.Parse("""{"type":"control_request","request_id":"1","request":{"subtype":"can_use_tool"}}"""));
    }
}
