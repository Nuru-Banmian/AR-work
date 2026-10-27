import assert from 'node:assert/strict'
import http from 'node:http'
import { once } from 'node:events'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { test } from 'node:test'
import { createServer } from 'vite'

test('SSE proxy closes the browser stream when the receiver aborts', async () => {
  let upstreamSocket
  const receiver = http.createServer((request, response) => {
    response.writeHead(200, { 'Content-Type': 'text/event-stream' })
    response.write('event: ready\ndata: {}\n\n')
    upstreamSocket = response.socket
  })
  receiver.listen(0, '127.0.0.1')
  await once(receiver, 'listening')
  const previous = process.env.ARWORK_API_URL
  const cacheDir = await mkdtemp(join(tmpdir(), 'arwork-sse-test-'))
  process.env.ARWORK_API_URL = `http://127.0.0.1:${receiver.address().port}`
  let vite, request, response
  try {
    vite = await createServer({ cacheDir, server: { host: '127.0.0.1', port: 0 }, logLevel: 'silent' })
    await vite.listen()
    request = http.get(`http://127.0.0.1:${vite.httpServer.address().port}/api/events`)
    ;[response] = await once(request, 'response')
    response.on('error', () => {}) // A mid-frame disconnect is expected here.
    await once(response, 'data')
    const closed = new Promise(resolve => response.once('close', () => resolve(true)))
    upstreamSocket.destroy() // Simulates a receiver process crash after headers.
    const timer = setTimeout(() => response.destroy(), 1500)
    let timeout
    try {
      const result = await Promise.race([closed, new Promise(resolve => { timeout = setTimeout(() => resolve(false), 1000) })])
      assert.equal(result, true, 'browser stream remained open after upstream abort; EventSource cannot reconnect')
    } finally { clearTimeout(timer); clearTimeout(timeout) }
  } finally {
    request?.destroy(); response?.destroy(); upstreamSocket?.destroy()
    await vite?.close()
    receiver.closeAllConnections()
    await new Promise(resolve => receiver.close(resolve))
    if (previous === undefined) delete process.env.ARWORK_API_URL
    else process.env.ARWORK_API_URL = previous
    assert.equal(dirname(cacheDir), resolve(tmpdir()))
    await rm(cacheDir, { recursive: true, force: true })
  }
})
