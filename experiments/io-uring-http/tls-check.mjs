import assert from 'node:assert/strict';
import net from 'node:net';
import tls from 'node:tls';
import { readFileSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';

const port = 18996;
const request = 'GET / HTTP/1.1\r\nHost: localhost\r\n\r\n';
const closeRequest = 'GET / HTTP/1.1\r\nHost: localhost\r\nCoNnEcTiOn: keep-alive, ClOsE\r\n\r\n';
const peers = new Set();
const ca = readFileSync('tls-assets/cert.pem');
let version = 'TLSv1.3';

async function connect() {
    const socket = tls.connect({ port, host: '127.0.0.1', servername: 'localhost', ca, minVersion: version, maxVersion: version });
    peers.add(socket);
    let buffer = Buffer.alloc(0), ended = false, error;
    socket.on('data', bytes => { buffer = Buffer.concat([buffer, bytes]); });
    socket.on('end', () => { ended = true; });
    socket.on('error', value => { error = value; });
    await new Promise((resolve, reject) => {
        socket.once('secureConnect', resolve);
        socket.once('error', reject);
    });
    assert.equal(socket.getProtocol(), version);
    return {
        socket,
        async response(code = 200, close = false) {
            const deadline = Date.now() + 5000;
            while (Date.now() < deadline) {
                if (error) throw error;
                const end = buffer.indexOf('\r\n\r\n');
                if (end >= 0) {
                    const header = buffer.subarray(0, end).toString();
                    const length = Number(header.match(/Content-Length: (\d+)/i)?.[1]);
                    assert.ok(Number.isInteger(length));
                    if (buffer.length >= end + 4 + length) {
                        assert.match(header, new RegExp(`^HTTP/1\\.[01] ${code} `));
                        if (close !== null)
                            assert.match(header, new RegExp(`Connection: ${close ? 'close' : 'keep-alive'}`, 'i'));
                        assert.equal(buffer.subarray(end + 4, end + 4 + length).toString(), code === 200 ? 'Hello world\n' : '');
                        buffer = buffer.subarray(end + 4 + length);
                        return;
                    }
                }
                if (ended) throw Error('EOF before full response');
                await delay(1);
            }
            throw Error('Response timeout');
        },
        async eof() {
            const deadline = Date.now() + 5000;
            while (!ended && Date.now() < deadline) {
                if (error) throw error;
                await delay(1);
            }
            assert.ok(ended, 'server must close');
            assert.equal(buffer.length, 0, 'no unexpected extra responses');
            socket.destroy();
            peers.delete(socket);
        },
        async quiet() {
            await delay(25);
            assert.equal(buffer.length, 0, 'must wait for complete request');
            assert.equal(ended, false);
        }
    };
}

async function test(binary) {
    let log = '';
    const child = spawn(binary, [String(port), 'tls-assets/cert.pem', 'tls-assets/key.pem'],
        { env: { ...process.env, TLS_TEST_SNDBUF: '4096' } });
    child.stdout.on('data', chunk => { log += chunk; });
    child.stderr.on('data', chunk => { log += chunk; });
    const exited = new Promise(resolve => child.once('exit', (code, signal) => resolve({code, signal})));
    try {
        for (let i = 0; i < 500 && !log.includes('READY '); i++) {
            if (child.exitCode !== null) throw Error(log);
            await delay(10);
        }
        assert.match(log, /READY /);
        let peer = await connect();
        peer.socket.write('GET / HTTP/1.1\r\nHost:');
        await peer.quiet();
        peer.socket.write(' localhost\r\n\r\n');
        await peer.response();
        for (let i = 0; i < 100; i++) {
            peer.socket.write(request);
            await peer.response();
        }
        peer.socket.write(closeRequest);
        await peer.response(200, true);
        await peer.eof();

        peer = await connect();
        peer.socket.pause();
        peer.socket.write(request.repeat(3000) + closeRequest);
        await delay(400);
        peer.socket.resume();
        for (let i = 0; i < 3000; i++) await peer.response();
        await peer.response(200, true);
        await peer.eof();

        peer = await connect();
        peer.socket.write(request.repeat(20) + closeRequest + request);
        for (let i = 0; i < 20; i++) await peer.response();
        await peer.response(200, true);
        await peer.eof();

        peer = await connect();
        peer.socket.write('POST / HTTP/1.1\r\nHost: localhost\r\nContent-Length: 40000\r\n\r\n' + 'a'.repeat(20000));
        await peer.quiet();
        peer.socket.write('a'.repeat(20000) + closeRequest);
        await peer.response();
        await peer.response(200, true);
        await peer.eof();

        peer = await connect();
        peer.socket.write('GET / HTTP/1.0\r\n\r\n');
        await peer.response(200, true);
        await peer.eof();
        peer = await connect();
        peer.socket.write('GET / HTTP/1.0\r\nConnection: keep-alive\r\n\r\n');
        await peer.response();
        peer.socket.write('GET / HTTP/1.0\r\nConnection: close\r\n\r\n');
        await peer.response(200, true);
        await peer.eof();

        for (const bad of [
            'GET / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n',
            'GET / HTTP/1.1\r\nContent-Length: nope\r\n\r\n',
            'GET / HTTP/1.1\r\nContent-Length: 1\r\nContent-Length: 1\r\n\r\n',
            'GET / HTTP/1.1\r\nContent-Length: 9999999999999999999999\r\n\r\n',
            'GET / HTTP/1.1\r\nConnection: disclose\r\nContent-Length: -1\r\n\r\n',
        ]) {
            peer = await connect();
            peer.socket.write(bad);
            await peer.response(400, true);
            await peer.eof();
        }
        peer = await connect();
        peer.socket.write('GET / HTTP/1.1\r\nConnection: disclose\r\n\r\n');
        await peer.response();
        peer.socket.write(closeRequest);
        await peer.response(200, true);
        await peer.eof();

        peer = await connect();
        peer.socket.end(request.repeat(4));
        for (let i = 0; i < 4; i++) {
            // EOF may be observed before or after the final response is queued.
            await peer.response(200, i < 3 ? false : null);
        }
        await peer.eof();

        peer = await connect();
        peer.socket.write(request);
        await peer.response();
        peer.socket.destroy(); // TCP ends without a TLS close_notify.
        peers.delete(peer.socket);
        for (let i = 0; i < 100 && !log.includes('peer abort'); i++) await delay(10);
        assert.match(log, /peer abort/);

        const idle = [];
        for (let i = 0; i < 32; i++) idle.push(await connect());
        // Also cancel an accepted socket which has not sent any TLS handshake.
        const raw = net.connect(port, '127.0.0.1');
        await new Promise(resolve => raw.once('connect', resolve));
        await delay(30);
        child.kill('SIGTERM');
        // Administrative shutdown can abort TLS; it must still drain operations.
        for (const item of idle) item.socket.destroy();
        raw.destroy();
        const result = await Promise.race([exited, delay(10000).then(() => { throw Error('shutdown timeout'); })]);
        assert.equal(result.code, 0);
        assert.match(log, /tls_errors=0 io_errors=0 peer_aborts=1 /);
        assert.match(log, /resumed=0 /);
        if (binary.includes('tls-fd'))
            assert.ok(Number(log.match(/want_write=(\d+)/)?.[1]) > 0, 'exercise WANT_WRITE');
        else
            assert.ok(Number(log.match(/partial_sends=(\d+)/)?.[1]) > 0, 'exercise partial ciphertext sends');
        assert.doesNotMatch(log, /ERROR: AddressSanitizer|runtime error:/);
        console.log(`PASS ${binary}\n${log}`);
    } finally {
        for (const peer of peers) peer.destroy();
        peers.clear();
        if (child.exitCode === null) child.kill('SIGKILL');
        await exited;
    }
}

for (version of ['TLSv1.2', 'TLSv1.3'])
    for (const binary of process.argv.slice(2)) await test(binary);
