// Stand-in Visual Studio bridge for tests.
//
// The extension's real bridge is a C# HttpListener. Driving the permission hook against a
// PowerShell HttpListener proved unreliable (the client reported a closed connection and the
// request never arrived), so the bench serves the endpoint from Node instead: same three
// routes, same JSON, same header check, and nothing about the hook changes.
//
// Usage: node fake-bridge.mjs <port> <token> <workspace-folder> <log-file>
// Prints "READY <port>" once listening; appends one JSON line per request to the log file
// and always answers {"accept":true}.

import { createServer } from 'node:http';
import { appendFileSync, writeFileSync } from 'node:fs';

const [port, token, workspace, logFile] = process.argv.slice(2);
if (!port || !token) {
  console.error('usage: node fake-bridge.mjs <port> <token> <workspace> <log>');
  process.exit(2);
}

writeFileSync(logFile, '');

const server = createServer((req, res) => {
  let body = '';
  req.setEncoding('utf8');
  req.on('data', (chunk) => { body += chunk; });
  req.on('end', () => {
    const authorized = req.headers['x-dsh-vs-authorization'] === token;
    appendFileSync(logFile, JSON.stringify({
      url: req.url,
      authorized,
      body,
    }) + '\n');

    if (!authorized) {
      res.writeHead(401, { 'content-type': 'application/json' });
      res.end('{"error":"unauthorized"}');
      return;
    }
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end('{"accept":true}');
  });
});

server.listen(Number(port), '127.0.0.1', () => {
  console.log(`READY ${port} ${workspace ?? ''}`);
});
