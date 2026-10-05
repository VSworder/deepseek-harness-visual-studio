// Protocol smoke test: drives the Visual Studio bridge's /mcp endpoint with the exact
// MCP client library DeepSeek Harness uses, so the handshake, tool listing and a tool
// call are verified against a real client rather than a hand-rolled approximation.
//
// Usage: node mcp-sdk-test.mjs <port> <token>

import { Client } from '@modelcontextprotocol/client';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/client';

const port = process.argv[2];
const token = process.argv[3];
if (!port || !token) {
  console.error('usage: node mcp-sdk-test.mjs <port> <token>');
  process.exit(2);
}

const transport = new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${port}/mcp`), {
  requestInit: { headers: { 'x-dsh-vs-authorization': token } },
});

const client = new Client({ name: 'mcp-sdk-test', version: '1.0.0' });

function report(label, fn) {
  return fn().then(
    (value) => console.log(`OK   ${label}  ${value}`),
    (error) => console.log(`FAIL ${label}  ${error.message}`),
  );
}

try {
  await report('connect', async () => {
    await client.connect(transport);
    return 'connected';
  });

  await report('tools/list', async () => {
    const result = await client.listTools();
    const names = (result.tools || []).map((t) => t.name);
    return `${names.length} tool(s): ${names.join(', ') || '(none)'}`;
  });

  const listed = await client.listTools().catch(() => ({ tools: [] }));
  for (const tool of listed.tools || []) {
    await report(`tools/call ${tool.name}`, async () => {
      const result = await client.callTool({ name: tool.name, arguments: {} });
      const text = (result.content || []).map((c) => c.text ?? '').join(' ');
      return (result.isError ? 'isError=true ' : '') + text.slice(0, 300);
    });
  }

  await client.close();
} catch (error) {
  console.log('FAIL fatal  ' + error.message);
  process.exit(1);
}
