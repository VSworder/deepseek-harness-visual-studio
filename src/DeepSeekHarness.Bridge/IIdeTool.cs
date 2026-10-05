using System;
using System.Collections.Generic;
using System.Text;

namespace DeepSeekHarness.Bridge
{
    /// <summary>
    /// One piece of Visual Studio state exposed to the agent as an MCP tool.
    /// </summary>
    /// <remarks>
    /// Implementations run on the Visual Studio UI thread: the endpoint marshals before
    /// invoking them, so an implementation can touch editors, the solution and the
    /// difference service without doing its own switching.
    /// </remarks>
    public interface IIdeTool
    {
        /// <summary>Wire name; the model sees it as <c>mcp__&lt;server&gt;__&lt;Name&gt;</c>.</summary>
        string Name { get; }

        /// <summary>Shown to the model, so it has to say when to call this.</summary>
        string Description { get; }

        /// <summary>
        /// JSON Schema for the arguments, as a JSON object. The endpoint forwards it
        /// verbatim, and a malformed schema would reject the whole tool list.
        /// </summary>
        string InputSchemaJson { get; }

        /// <summary>
        /// Produces the tool result text. The endpoint wraps it in the MCP content shape;
        /// throwing is reported to the model as an error result rather than a crash.
        /// </summary>
        string Invoke(IReadOnlyDictionary<string, string> arguments);
    }

    /// <summary>
    /// Builds the JSON Schema objects the MCP tool list needs.
    /// </summary>
    public static class ToolSchema
    {
        /// <summary>A schema for a tool that takes no arguments.</summary>
        public static string None()
        {
            return "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        }
    }
}
