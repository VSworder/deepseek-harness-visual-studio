// Standalone harness for the bridge's MCP surface. Starts the real BridgeServer with two
// stand-in IDE tools, prints the port and token, and stays up until the parent kills it.
//
// This exists so the Model Context Protocol layer can be tested without Visual Studio in
// the loop: the protocol is the part most likely to be wrong, and it is the part the agent
// actually depends on.
//
// Written in C# 5 syntax because it is compiled by the in-box csc.exe, which predates
// expression-bodied members.

using System;
using System.Collections.Generic;
using System.Threading;
using DeepSeekHarness.Bridge;

internal sealed class FakeTool : IIdeTool
{
    private readonly string _name;
    private readonly string _description;
    private readonly string _text;

    public FakeTool(string name, string description, string text)
    {
        _name = name;
        _description = description;
        _text = text;
    }

    public string Name { get { return _name; } }
    public string Description { get { return _description; } }
    public string InputSchemaJson { get { return ToolSchema.None(); } }

    public string Invoke(IReadOnlyDictionary<string, string> arguments)
    {
        return _text;
    }
}

internal static class Harness
{
    private static int Main(string[] args)
    {
        // The workspace root comes from the command line: hardcoding one machine's layout
        // meant the harness passed while proving nothing about path matching on anyone
        // else's disk, and reported a workspace that does not exist for them.
        var workspaceRoot = args != null && args.Length > 0 ? args[0] : Environment.CurrentDirectory;

        var tools = new IIdeTool[]
        {
            new FakeTool(
                "get_environment",
                "Reports which IDE and workspace the bridge is attached to.",
                "Visual Studio harness\r\nworkspace: " + workspaceRoot + "\r\nfile: Example.cs"),
            new FakeTool(
                "get_current_selection",
                "Returns the text currently selected in the editor.",
                "Example.cs lines 32-35:\r\n[Header(\"horizontal move\")]")
        };

        var matcher = new PathWorkspaceMatcher(new[] { workspaceRoot });
        var server = new BridgeServer(matcher, delegate(string message) { Console.Error.WriteLine("[bridge] " + message); });

        server.ToolProvider = delegate { return tools; };
        server.ToolInvoker = delegate(IIdeTool tool, IReadOnlyDictionary<string, string> arguments)
        {
            return tool.Invoke(arguments);
        };

        server.Start();
        Console.WriteLine("PORT " + server.Port);
        Console.WriteLine("TOKEN " + server.AuthToken);
        Console.Out.Flush();

        // Keep serving until killed.
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
