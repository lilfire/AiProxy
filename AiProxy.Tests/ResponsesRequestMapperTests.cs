using System.Text.Json;
using AiProxy.Application.Services;
using AiProxy.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests;

[TestClass]
public class ResponsesRequestMapperTests
{
    private ResponsesRequestMapper CreateMapper()
    {
        return new ResponsesRequestMapper(NullLogger<ResponsesRequestMapper>.Instance);
    }

    [TestMethod]
    public void MapToChatRequest_with_string_input_creates_user_message()
    {
        var mapper = CreateMapper();
        var request = new OpenAiResponsesRequest
        {
            Model = "test-model",
            Input = "Hello"
        };

        var result = mapper.MapToChatRequest(request);

        Assert.AreEqual("test-model", result.Model);
        Assert.AreEqual(1, result.Messages.Count);
        Assert.AreEqual("user", result.Messages[0].Role);
        Assert.AreEqual("Hello", result.Messages[0].Content);
    }

    [TestMethod]
    public void MapToChatRequest_with_json_string_input_creates_user_message()
    {
        var mapper = CreateMapper();
        var element = JsonSerializer.SerializeToElement("Hello");
        var request = new OpenAiResponsesRequest
        {
            Model = "test-model",
            Input = element
        };

        var result = mapper.MapToChatRequest(request);

        Assert.AreEqual("user", result.Messages[0].Role);
        Assert.AreEqual("Hello", result.Messages[0].Content);
    }

    [TestMethod]
    public void MapToChatRequest_with_input_text_array_creates_user_message()
    {
        var mapper = CreateMapper();
        var element = JsonSerializer.SerializeToElement(new[]
        {
            new { type = "input_text", text = "Hello" }
        });
        var request = new OpenAiResponsesRequest
        {
            Model = "test-model",
            Input = element
        };

        var result = mapper.MapToChatRequest(request);

        Assert.AreEqual(1, result.Messages.Count);
        Assert.AreEqual("user", result.Messages[0].Role);
        Assert.AreEqual("Hello", result.Messages[0].Content);
    }

    [TestMethod]
    public void MapToChatRequest_with_message_array_uses_role_and_extracts_text()
    {
        var mapper = CreateMapper();
        var element = JsonSerializer.SerializeToElement(new[]
        {
            new
            {
                type = "message",
                role = "assistant",
                content = new[] { new { type = "output_text", text = "Hi there" } }
            }
        });
        var request = new OpenAiResponsesRequest
        {
            Model = "test-model",
            Input = element
        };

        var result = mapper.MapToChatRequest(request);

        Assert.AreEqual(1, result.Messages.Count);
        Assert.AreEqual("assistant", result.Messages[0].Role);
        Assert.AreEqual("Hi there", result.Messages[0].Content);
    }

    [TestMethod]
    public void MapToChatRequest_with_input_image_preserves_the_image()
    {
        var element = JsonSerializer.SerializeToElement(new[]
        {
            new
            {
                type = "message",
                role = "user",
                content = new object[]
                {
                    new { type = "input_text", text = "Beskriv bildet" },
                    new { type = "input_image", image_url = "data:image/png;base64,iVBORw0KGgo=" }
                }
            }
        });

        var result = CreateMapper().MapToChatRequest(new OpenAiResponsesRequest { Model = "test-model", Input = element });

        Assert.AreEqual("Beskriv bildet", result.Messages[0].Content);
        Assert.AreEqual(1, result.Messages[0].Images.Count);
    }

    [TestMethod]
    public void MapToChatRequest_with_opencode_message_without_type_preserves_the_image()
    {
        var element = JsonSerializer.SerializeToElement(new[]
        {
            new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "input_text", text = "Beskriv bildet" },
                    new { type = "input_image", image_url = new { url = "data:image/png;base64,iVBORw0KGgo=" } }
                }
            }
        });

        var result = CreateMapper().MapToChatRequest(new OpenAiResponsesRequest { Model = "test-model", Input = element });

        Assert.AreEqual("user", result.Messages[0].Role);
        Assert.AreEqual("Beskriv bildet", result.Messages[0].Content);
        Assert.AreEqual(1, result.Messages[0].Images.Count);
    }

    [TestMethod]
    public void MapToChatRequest_with_standalone_input_image_preserves_the_image()
    {
        var element = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "input_text", text = "Beskriv bildet" },
            new { type = "input_image", image_url = new { url = "data:image/png;base64,iVBORw0KGgo=" } }
        });

        var result = CreateMapper().MapToChatRequest(new OpenAiResponsesRequest { Model = "test-model", Input = element });

        Assert.AreEqual(2, result.Messages.Count);
        Assert.AreEqual("Beskriv bildet", result.Messages[0].Content);
        Assert.AreEqual(1, result.Messages[1].Images.Count);
        Assert.AreEqual("data:image/png;base64,iVBORw0KGgo=", result.Messages[1].Images[0].Url);
    }

    [TestMethod]
    public void MapToChatRequest_with_standalone_image_file_preserves_the_image()
    {
        var element = JsonSerializer.SerializeToElement(new[]
        {
            new { type = "input_file", filename = "clipboard.png", file_data = "iVBORw0KGgo=" }
        });

        var result = CreateMapper().MapToChatRequest(new OpenAiResponsesRequest { Model = "test-model", Input = element });

        Assert.AreEqual(1, result.Messages[0].Images.Count);
        Assert.AreEqual("data:image/png;base64,iVBORw0KGgo=", result.Messages[0].Images[0].Url);
    }

    [TestMethod]
    public void MapToChatRequest_with_empty_input_adds_empty_user_message()
    {
        var mapper = CreateMapper();
        var request = new OpenAiResponsesRequest
        {
            Model = "test-model",
            Input = null
        };

        var result = mapper.MapToChatRequest(request);

        Assert.AreEqual(1, result.Messages.Count);
        Assert.AreEqual("user", result.Messages[0].Role);
        Assert.AreEqual(string.Empty, result.Messages[0].Content);
    }

    [TestMethod]
    public void MapToChatRequest_with_unknown_item_type_falls_back_to_user_message()
    {
        var mapper = CreateMapper();
        var element = JsonSerializer.SerializeToElement(new[]
        {
            new { type = "image_file", file_id = "file-123" }
        });
        var request = new OpenAiResponsesRequest
        {
            Model = "test-model",
            Input = element
        };

        var result = mapper.MapToChatRequest(request);

        Assert.AreEqual(1, result.Messages.Count);
        Assert.AreEqual("user", result.Messages[0].Role);
        Assert.AreEqual(string.Empty, result.Messages[0].Content);
    }

    [TestMethod]
    public void MapToChatRequest_skips_function_call_output_items()
    {
        var mapper = CreateMapper();
        var element = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "message", role = "user", content = "Hei" },
            new { type = "function_call_output", call_id = "call_aiproxy_todo_1", output = "{\"ok\":true}" }
        });
        var request = new OpenAiResponsesRequest
        {
            Model = "test-model",
            Input = element
        };

        var result = mapper.MapToChatRequest(request);

        Assert.AreEqual(1, result.Messages.Count);
        Assert.AreEqual("Hei", result.Messages[0].Content);
    }

    [TestMethod]
    public void MapToChatRequest_skips_function_call_items()
    {
        var mapper = CreateMapper();
        var element = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "message", role = "user", content = "Hei" },
            new { type = "function_call", call_id = "call_aiproxy_todo_1", name = "todowrite", arguments = "{}" }
        });
        var request = new OpenAiResponsesRequest
        {
            Model = "test-model",
            Input = element
        };

        var result = mapper.MapToChatRequest(request);

        Assert.AreEqual(1, result.Messages.Count);
        Assert.AreEqual("Hei", result.Messages[0].Content);
    }

    [TestMethod]
    public void MapToChatRequest_preserves_function_schema_choice_calls_and_results_for_tool_provider()
    {
        var input = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "function_call", call_id = "call_client_1", name = "read_file", arguments = "{\"path\":\"a.txt\"}" },
            new { type = "function_call_output", call_id = "call_client_1", output = "contents" }
        });
        var request = new OpenAiResponsesRequest
        {
            Model = "test-model",
            Input = input,
            ToolChoice = JsonSerializer.SerializeToElement(new { type = "function", name = "read_file" }),
            Tools = [new OpenAiResponsesTool
            {
                Type = "function",
                Name = "read_file",
                Description = "Read a file",
                Parameters = JsonSerializer.SerializeToElement(new { type = "object", properties = new { path = new { type = "string" } } })
            }]
        };

        var result = CreateMapper().MapToChatRequest(request);

        Assert.AreEqual(1, result.FunctionTools.Count);
        Assert.AreEqual("read_file", result.FunctionTools[0].Name);
        Assert.IsTrue(result.ToolChoice.HasValue);
        Assert.AreEqual("read_file", result.ToolChoice!.Value.GetProperty("name").GetString());
        Assert.AreEqual("call_client_1", result.PreviousToolCalls.Single().Id);
        Assert.AreEqual("contents", result.ToolResults.Single().Output);
    }

    [TestMethod]
    public void MapToChatRequest_preserves_custom_tools_for_tool_provider()
    {
        var request = new OpenAiResponsesRequest
        {
            Model = "test-model",
            Input = JsonSerializer.SerializeToElement("List files"),
            Tools = [new OpenAiResponsesTool { Type = "custom", Name = "glob", Description = "Lists matching paths" }]
        };

        var result = CreateMapper().MapToChatRequest(request);

        Assert.AreEqual(1, result.FunctionTools.Count);
        Assert.AreEqual("glob", result.FunctionTools.Single().Name);
        Assert.AreEqual("custom", result.FunctionTools.Single().Type);
    }

    [TestMethod]
    public void MapToChatRequest_removes_messages_without_content()
    {
        var mapper = CreateMapper();
        var element = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "message", role = "user", content = "Hei" },
            new { type = "message", role = "assistant", content = "" }
        });
        var request = new OpenAiResponsesRequest
        {
            Model = "test-model",
            Input = element
        };

        var result = mapper.MapToChatRequest(request);

        Assert.AreEqual(1, result.Messages.Count);
        Assert.AreEqual("user", result.Messages[0].Role);
    }
}
