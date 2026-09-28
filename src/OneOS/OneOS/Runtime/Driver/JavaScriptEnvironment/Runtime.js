/* Represents the host Runtime */
const fs = require('fs');
const net = require('net');
const child_process = require('child_process');
const stream = require('stream');
const events = require('events');

// Helpers
const ID_CHARSET = 'abcdefghijklmnopqrstuvwxyz';
function randstr(length = 8) {
    return Array.from({ length: length }, item => ID_CHARSET[Math.floor(Math.random() * ID_CHARSET.length)]).join('');
}

// --- IPC with the OneOS runtime (OneOS-V6, plan step 7) ---
//
// The runtime hands the process its channels as inherited file descriptors, listed in ONEOS_IPC_FDS as
// "<control in>,<control out>,<sync in>,<sync out>" (FIFOs, opened before the program starts; no sockets,
// listeners or handshakes). Both channels carry frames:
//     [u32 LE length][u8 kind][body]      length = 1 + body length
//     'J'  a JSON message: { type: 'call', transactionId, method, arguments }
//                       or { type: 'return', transactionId, hasError, result }
//     'D'  stream data:    [u32 LE stream id][bytes]
//     'E'  end of stream:  [u32 LE stream id][optional UTF-8 error message]
// The control channel is asynchronous (RPC both ways, and every stream, multiplexed by stream id). The sync
// channel is only ever read with fs.readSync: one blocking request/response for synchronous APIs.

const FRAME_JSON = 0x4A, FRAME_DATA = 0x44, FRAME_END = 0x45;

function encodeFrame(kind, body) {
    const frame = Buffer.alloc(5 + body.length);
    frame.writeUInt32LE(body.length + 1, 0);
    frame[4] = kind;
    body.copy(frame, 5);
    return frame;
}

function streamFrame(kind, streamId, payload) {
    const body = Buffer.alloc(4 + payload.length);
    body.writeUInt32LE(streamId, 0);
    payload.copy(body, 4);
    return encodeFrame(kind, body);
}

function ipcDescriptors() {
    const fds = String(process.env['ONEOS_IPC_FDS'] || '').split(',').map(Number);
    if (fds.length !== 4 || fds.some(fd => !Number.isInteger(fd) || fd < 3))
        throw new Error('oneos: ONEOS_IPC_FDS is missing or malformed; is this program running as a OneOS JavaScript agent?');
    return { controlIn: fds[0], controlOut: fds[1], syncIn: fds[2], syncOut: fds[3] };
}

class IpcChannel extends events.EventEmitter {
    constructor(inFd, outFd) {
        super();
        this.input = new net.Socket({ fd: inFd, readable: true, writable: false });
        this.output = new net.Socket({ fd: outFd, readable: false, writable: true });
        this.streams = new Map();       // stream id -> { data(buffer), end(error) }
        this.early = new Map();         // stream id -> frames that arrived before the stream was registered
        this.holds = 0;
        this.input.on('data', createMessageReceiver(frame => this._onFrame(frame)));
        this.input.on('end', () => this.emit('close'));
        // The channel alone doesn't keep the program alive: only pending requests and open streams do.
        this.input.unref();
    }

    hold() {
        if (this.holds++ === 0) this.input.ref();
    }

    release() {
        if (this.holds > 0 && --this.holds === 0) this.input.unref();
    }

    sendMessage(message) {
        this.output.write(encodeFrame(FRAME_JSON, Buffer.from(JSON.stringify(message))));
    }

    sendData(streamId, payload, callback) {
        this.output.write(streamFrame(FRAME_DATA, streamId, Buffer.isBuffer(payload) ? payload : Buffer.from(payload)), callback);
    }

    endStream(streamId, error) {
        this.output.write(streamFrame(FRAME_END, streamId, Buffer.from(error ? String(error) : '')));
    }

    // Data the runtime sends on a stream. Frames can arrive before the RPC that opened the stream has
    // returned to its caller; they wait here until the stream is registered.
    registerStream(streamId, handlers) {
        this.streams.set(streamId, handlers);
        this.hold();
        const early = this.early.get(streamId);
        if (early) {
            this.early.delete(streamId);
            early.forEach(frame => this._onFrame(frame));
        }
    }

    _onFrame(frame) {
        const kind = frame[0];
        if (kind === FRAME_JSON) {
            this.emit('message', JSON.parse(String(frame.subarray(1))));
            return;
        }
        const streamId = frame.readUInt32LE(1);
        const stream = this.streams.get(streamId);
        if (!stream) {
            if (!this.early.has(streamId)) this.early.set(streamId, []);
            this.early.get(streamId).push(frame);
            return;
        }
        if (kind === FRAME_DATA) {
            stream.data(frame.subarray(5));
        }
        else if (kind === FRAME_END) {
            this.streams.delete(streamId);
            this.release();
            const error = String(frame.subarray(5));
            stream.end(error ? new Error(error) : null);
        }
    }
}

function createMessageReceiver(onData) {
    let header = Buffer.alloc(4);
    let headerRead = 0;
    let frame = null;
    let frameCursor = 0;

    return chunk => {
        let bytesRead = chunk.length;
        let cursor = 0;
        while (cursor < bytesRead) {
            while (frame === null) {
                header[headerRead] = chunk[cursor];
                headerRead++;
                cursor++;
                if (headerRead === 4) {
                    frame = Buffer.alloc(header.readInt32LE());
                    frameCursor = 0;
                    headerRead = 0;
                }
                if (cursor >= bytesRead) break;
            }
            if (cursor >= bytesRead) break;

            let frameBytesLeft = frame.length - frameCursor;
            let bytesLeft = bytesRead - cursor;

            if (frameBytesLeft <= bytesLeft) {
                chunk.copy(frame, frameCursor, cursor, cursor + frameBytesLeft);
                onData(frame);

                cursor += frameBytesLeft;
                frame = null;
            }
            else {
                chunk.copy(frame, frameCursor, cursor, cursor + bytesLeft);
                cursor += bytesLeft;
                frameCursor += bytesLeft;
            }
        }
    }
}

// Reads JSON objects from a byte stream: newline-delimited (what process.stdout.json writes) or simply
// concatenated (OneOS-V5). Braces inside strings don't count.
function createJsonReceiver(onValue) {
    let parts = [];
    let depth = 0;
    let inString = false;
    let escaped = false;

    return buffer => {
        let start = depth > 0 ? 0 : -1;
        for (let i = 0; i < buffer.length; i++) {
            const c = buffer[i];
            if (inString) {
                if (escaped) escaped = false;
                else if (c === 0x5C) escaped = true;
                else if (c === 0x22) inString = false;
                continue;
            }
            if (c === 0x22) {
                if (depth > 0) inString = true;
            }
            else if (c === 0x7B) {
                if (depth === 0) start = i;
                depth += 1;
            }
            else if (c === 0x7D && depth > 0) {
                depth -= 1;
                if (depth === 0) {
                    parts.push(buffer.subarray(start, i + 1));
                    const text = String(Buffer.concat(parts));
                    parts = [];
                    start = -1;
                    let value;
                    try { value = JSON.parse(text); }
                    catch (err) { console.error(`oneos: skipping malformed JSON: ${err.message}`); continue; }
                    onValue(value);
                }
            }
        }
        if (depth > 0 && start >= 0) parts.push(Buffer.from(buffer.subarray(start)));
    }
}

const PROPERTY_PREFIX = 'τ';		// prefix for injected properties

class MessageStream extends stream.Duplex {
    constructor(inputByteStream, outputByteStream) {
        super();
        this.__receive = createMessageReceiver(frame => this.push(frame));

        if (inputByteStream) this.setInputStream(inputByteStream);
        if (outputByteStream) this.setOutputStream(outputByteStream);
    }

    setInputStream(inputByteStream) {
        this.input = inputByteStream;
        this._bindInput();
    }

    setOutputStream(outputByteStream) {
        this.output = outputByteStream;
    }

    _bindInput() {
        /*let header = Buffer.alloc(4);
        let headerRead = 0;
        let frame = null;
        let frameCursor = 0;

        this.input.on('data', chunk => {
            let bytesRead = chunk.length;
            let cursor = 0;
            while (cursor < bytesRead) {
                while (frame === null) {
                    header[headerRead] = chunk[cursor];
                    headerRead++;
                    cursor++;
                    if (headerRead === 4) {
                        frame = Buffer.alloc(header.readInt32LE());
                        frameCursor = 0;
                        headerRead = 0;
                    }
                    if (cursor >= bytesRead) break;
                }
                if (cursor >= bytesRead) break;

                let frameBytesLeft = frame.length - frameCursor;
                let bytesLeft = bytesRead - cursor;

                if (frameBytesLeft <= bytesLeft) {
                    chunk.copy(frame, frameCursor, cursor, cursor + frameBytesLeft);
                    this.push(frame);

                    cursor += frameBytesLeft;
                    frame = null;
                }
                else {
                    chunk.copy(frame, frameCursor, cursor, cursor + bytesLeft);
                    cursor += bytesLeft;
                    frameCursor += bytesLeft;
                }
            }
        });*/

        this.input.on('data', this.__receive);

        this.input.on('end', () => {
            this.push(null);
        });
    }

    _read(size) {
        // do nothing, we assume that the input is in flowing mode
    }

    _write(payload, encoding, callback) {
        let header = Buffer.from([0, 0, 0, 0]);
        header.writeInt32LE(payload.length, 0);
        this.output.write(Buffer.concat([header, payload]), encoding, callback);
    }
}

class JsonStream extends stream.Duplex {
    constructor(inputByteStream, outputByteStream) {
        super({
            readableObjectMode: true,
            writableObjectMode: true
        });

        if (inputByteStream) this.setInputStream(inputByteStream);
        if (outputByteStream) this.setOutputStream(outputByteStream);
    }

    setInputStream(inputByteStream) {
        this.input = inputByteStream;
        this._bindInput();
    }

    setOutputStream(outputByteStream) {
        this.output = outputByteStream;
    }

    _bindInput() {
        this.input.on('data', createJsonReceiver(value => this.push(value)));
        this.input.on('end', () => this.push(null));
    }

    _read(size) {
        // do nothing, we assume that the input is in flowing mode
    }

    _write(payload, encoding, callback) {
        // One JSON object per line (ndjson), the framing of record and `json` ports (L§5.2).
        this.output.write(JSON.stringify(payload) + '\n', callback);
    }
}

// We use a Duplex stream because using a Writable stream
// over the TCP abstraction makes it difficult to perform
// the initial handshake with the Runtime (Readable stream is okay).
// We rely on the Runtime to send a "Ready" message
// after we send it a connection request message
class DirectPipe extends stream.Duplex {
    constructor(type, mode, format = 'raw') {
        super({
            readableObjectMode: format === 'json'
        });

        this.type = type;
        this.mode = mode;
        this.format = format;

        this.connected = new Promise((resolve, reject) => {
            this.__resolve = resolve;
            this.__reject = reject;
        });

        if (this.mode === 'read') {
            if (this.format === 'segment') {
                // IO streams (e.g., video) expect segments over the TCP stream
                this.__receive = this.createSegmentedReceiver();
            }
            else if (this.format === 'json') {
                this.__receive = createJsonReceiver(value => this.push(value));
            }
            else if (this.format === 'mjpeg') {
                this.__receive = this.createMjpegReceiver();
            }
            else {
                this.__receive = data => {
                    this.push(data);
                    //this.bytesRead += data.length;
                };
            }

            this.bytesRead = 0;
        }
    }

    createSegmentedReceiver() {
        let cursor = 0;
        let frame = null;
        let header = Buffer.alloc(4);
        let headerRead = 0;
        let frameCursor = 0;

        const receive = (data) => {
            cursor = 0;

            while (cursor < data.length) {
                // if we don't have a frame set, we are reading a new frame
                while (frame == null) {
                    header[headerRead] = data[cursor];
                    headerRead++;
                    cursor++;
                    if (headerRead == 4) {
                        frame = Buffer.alloc(header.readInt32LE(0));
                        frameCursor = 0;
                        headerRead = 0;
                    }
                    if (cursor >= data.length) break;
                }
                if (cursor >= data.length) break;

                let frameBytesLeft = frame.length - frameCursor;
                let bytesLeft = data.length - cursor;

                // if the bytes left in the frame are less than or equal to
                // bytes left to read in the current read, we can read the whole frame
                if (frameBytesLeft <= bytesLeft) {
                    data.copy(frame, frameCursor, cursor, cursor + frameBytesLeft);
                    // flush the frame
                    this.push(frame);
                    //this.bytesRead += frame.length;

                    cursor += frameBytesLeft;   // advance the cursor
                    frame = null;               // clear the frame
                }
                else {
                    // if the bytes left in the frame is larger than the remaining bytes,
                    // read what is remaining and add to the frame
                    data.copy(frame, frameCursor, cursor, cursor + bytesLeft);

                    cursor += bytesLeft;        // advance the cursor
                    frameCursor += bytesLeft;   // advance the frame cursor
                }
            }
        }

        return receive;
    }

    // One JPEG per push, from start-of-image (FFD8) through end-of-image (FFD9). A marker may be split
    // across two chunks, and a frame may be any size.
    createMjpegReceiver() {
        let parts = [];
        let inFrame = false;
        let prev = -1;      // the last byte of the previous chunk

        const receive = (buffer) => {
            let start = inFrame ? 0 : -1;
            for (let i = 0; i < buffer.length; i++) {
                const before = i > 0 ? buffer[i - 1] : prev;
                if (!inFrame && before === 0xFF && buffer[i] === 0xD8) {
                    inFrame = true;
                    if (i === 0) { parts = [Buffer.from([0xFF])]; start = 0; }
                    else { parts = []; start = i - 1; }
                }
                else if (inFrame && before === 0xFF && buffer[i] === 0xD9) {
                    parts.push(buffer.subarray(start, i + 1));
                    this.push(Buffer.concat(parts));
                    parts = [];
                    inFrame = false;
                    start = -1;
                }
            }
            if (inFrame) parts.push(Buffer.from(buffer.subarray(start)));
            if (buffer.length > 0) prev = buffer[buffer.length - 1];
        }

        return receive;
    }

    // Attaches this stream to a stream the runtime opened on the control channel (the id an RPC returned).
    connect(streamId) {
        this.streamId = streamId;
        if (this.mode === 'read') {
            channel.registerStream(streamId, {
                data: data => this.__receive(data),
                end: err => {
                    if (err) this.destroy(err);
                    else this.push(null);
                }
            });
        }
        else channel.hold();    // released when the stream finishes
        this.__resolve(streamId);
    }

    _read(size) {
        // do nothing, we assume that the input is in flowing mode
    }

    _write(payload, encoding, callback) {
        if (this.mode === 'write') {
            this.connected.then(streamId => channel.sendData(streamId, payload, callback), callback);
        }
        else callback(new Error('this stream is read-only'));
    }

    _final(callback) {
        if (this.mode === 'write') {
            this.connected.then(streamId => {
                channel.endStream(streamId);
                channel.release();
                callback();
            }, callback);
        }
        else callback();
    }

    read(size) {
        const result = super.read(size);
        if (result) this.bytesRead += result.length;
        return result;
    }
}

class AgentTunnel extends MessageStream {
    constructor(sessionKey, agentUri, channelIn, channelOut) {
        super();
        this.uri = process.uri + '/tunnel/' + randstr();
        this.sessionKey = sessionKey;
        this.targetAgent = agentUri;
        this.targetChannelIn = channelIn;
        this.targetChannelOut = channelOut;
        this.messagesRead = 0;
        this.messagesWritten = 0;

        this.connected = new Promise((resolve, reject) => {
            this.__resolve = resolve;
            this.__reject = reject;
        });

        this.connect();
    }

    connect() {
        rpc.request('CreateAgentTunnel', this.targetAgent, this.targetChannelIn, this.targetChannelOut)
            .then(streamId => {
                // The tunnel's byte stream is stream `streamId` of the control channel, both ways.
                channel.registerStream(streamId, {
                    data: data => this.__receive(data),
                    end: err => err ? this.destroy(err) : this.push(null)
                });
                this.setOutputStream({ write: (payload, encoding, callback) => channel.sendData(streamId, payload, callback) });
                this.__resolve(streamId);
            }, err => {
                this.__reject(err);
            });
    }

    _read(size) {
        // do nothing, we assume that the input is in flowing mode
    }

    _write(payload, encoding, callback) {
        this.connected.then((client) => {
            //client.write(payload, encoding, callback);
            super._write(payload, encoding, callback);
            //super._write(this._createMessage(payload), encoding, callback);
        });
    }

    //_createMessage(payload) {
    //    const header = Buffer.from(`${this.uri};${this.uri}/stdout;${this.messagesWritten}`);
    //    const frame = Buffer.alloc(4 + header.length + payload.length);

    //    frame.writeInt32LE(header.length, 0);
    //    header.copy(frame, 4, 0, header.length);
    //    payload.copy(frame, 4 + header.length, 0, payload.length);

    //    return frame;
    //}

    //_parseMessage(frame) {
    //    const headerLength = frame.readInt32LE(0);
    //    const payload = Buffer.alloc(frame.length - 4 - headerLength);
    //    frame.copy(payload, 0, 4 + headerLength, frame.length);

    //    return payload;
    //}

    //push(frame) {
    //    super.push(this._parseMessage(frame));
    //}
}

class RpcSocket {
    constructor(channel, handlers = {}) {
        this.handlers = handlers;
        this.requests = {};
        this.channel = channel;

        channel.on('message', message => {
            if (message.type === 'call') {
                if (this.handlers[message.method]) {
                    try {
                        let result = this.handlers[message.method].apply(null, message.arguments);
                        channel.sendMessage(this.createResponse(message, false, result));
                    }
                    catch (err) {
                        channel.sendMessage(this.createResponse(message, true, err.stack));
                    }
                }
                else {
                    channel.sendMessage(this.createResponse(message, true, "Not a valid method"));
                }
            }
            else if (message.type === 'return') {
                if (this.requests[message.transactionId]) {
                    if (message.hasError) {
                        const err = new Error(message.result);
                        if (message.result === 'ENOENT') err.code = 'ENOENT';
                        this.requests[message.transactionId].reject(err);
                    }
                    else {
                        this.requests[message.transactionId].resolve(message.result);
                    }
                }
                else {
                    // ignore the message
                }
            }
        })
    }

    createResponse(requestMessage, hasError, result) {
        return {
            type: 'return',
            transactionId: requestMessage.transactionId,
            hasError: hasError,
            result: result
        }
    }

    setHandler(method, func) {
        this.handlers[method] = func;
    }

    request(method, ...args) {
        return new Promise((resolve, reject) => {
            let id = randstr();
            let request = {
                type: 'call',
                transactionId: id,
                method: method,
                arguments: args
            };

            // A pending request keeps the program alive until the runtime answers.
            this.channel.hold();
            this.requests[id] = {
                resolve: result => {
                    delete this.requests[id];
                    this.channel.release();
                    resolve(result);
                },
                reject: err => {
                    delete this.requests[id];
                    this.channel.release();
                    reject(err);
                }
            };

            this.channel.sendMessage(request);
        });
    }
}

// This RPC Request function is used in exceptional cases
// where the client-side API must remain synchronous
// e.g., readFileSync
// In most cases, RpcSocket should be used for efficiency.
// It blocks on the sync channel (fs.readSync on an inherited descriptor): one frame out, one frame back.
function readExactlySync(fd, length) {
    const buffer = Buffer.alloc(length);
    let read = 0;
    while (read < length) {
        const n = fs.readSync(fd, buffer, read, length - read, null);
        if (n === 0) throw new Error('oneos: the runtime closed the sync channel');
        read += n;
    }
    return buffer;
}

function syncRpcRequest(method, ...args) {
    const request = encodeFrame(FRAME_JSON, Buffer.from(JSON.stringify({
        type: 'call',
        transactionId: randstr(),
        method: method,
        arguments: args
    })));
    let written = 0;
    while (written < request.length) written += fs.writeSync(fds.syncOut, request, written, request.length - written);

    const length = readExactlySync(fds.syncIn, 4).readUInt32LE(0);
    const frame = readExactlySync(fds.syncIn, length);
    const message = JSON.parse(String(frame.subarray(1)));
    if (message.hasError) {
        const err = new Error(message.result);
        if (message.result === 'ENOENT') err.code = 'ENOENT';
        throw err;
    }
    return message.result;
}

let filename;
let connected;
let rpc;
let channel;
let fds;

const Runtime = {
    connect: (root) => {
        if (!connected) {
            connected = new Promise((resolve, reject) => {

                filename = root.meta.filename;

                const runtimeEventEmitter = new events.EventEmitter(); // event emitter used to notify about OneOS runtime events to the user process

                fds = ipcDescriptors();
                channel = new IpcChannel(fds.controlIn, fds.controlOut);
                rpc = new RpcSocket(channel, {
                    'pause': () => {
                        root.pauseTimers();
                        return null;
                    },
                    'resume': () => {
                        root.resumeTimers();
                        return null;
                    },
                    'checkpoint': () => {
                        return root.snapshot();
                    },
                    'emitRuntimeEvent': (evtType, evtData) => {
                        runtimeEventEmitter.emit('data', {
                            type: evtType,
                            data: evtData
                        });
                        return null;
                    }
                });

                // Override built-in API here, before yielding control to user program
                // - apart from the oneos/fs, oneos/net modules, we override the built-ins
                //   here to replace the evaluation context of third-party modules
                // - This works because this file (Runtime.js) is loaded before any other
                //   third-party modules, and thus is the first one to load the built-ins

                // override JSON.stringify so that oneos-instrumented properties are not
                // included in the result string
                const nativeJsonStringify = JSON.stringify;
                JSON.stringify = function (obj) {
                    return nativeJsonStringify.call(this, obj, (k, v) => ((!k || k[0] !== PROPERTY_PREFIX) ? v : undefined));
                }

                // override fs functions
                fs.readFile = function readFile(path, arg2, arg3) {
                    let encoding = 'raw', callback;
                    if (typeof arg2 === 'string') {
                        encoding = arg2;
                        callback = arg3;
                    }
                    else {
                        callback = arg2;
                    }
                    Runtime.readFile(path, encoding).then(data => {
                        callback(null, data);
                    }).catch(err => callback(err));
                }

                fs.writeFile = function writeFile(path, content, arg3, arg4) {
                    let encoding = 'raw', callback;
                    if (typeof arg3 === 'string') {
                        encoding = arg3;
                        callback = arg4;
                    }
                    else {
                        callback = arg3;
                    }
                    Runtime.writeFile(path, content, encoding).then(data => {
                        callback(null, data);
                    }).catch(err => callback(err));
                }

                fs.appendFile = function appendFile(path, content, callback) {
                    Runtime.appendFile(path, content).then(data => {
                        callback(null, data);
                    }).catch(err => callback(err));
                }

                fs.createReadStream = function createReadStream(path) {
                    return Runtime.createReadStream(path);
                }

                fs.createWriteStream = function createWriteStream(path) {
                    return Runtime.createWriteStream(path);
                }

                fs.stat = function stat(path, callback) {
                    return Runtime.fsStat(path).then(data => {
                        callback(null, data);
                    }).catch(err => callback(err));
                }

                fs.readdir = function readdir(path, options, callback) {
                    // fs.readdir(path, callback) as well as fs.readdir(path, options, callback)
                    if (typeof options === 'function') { callback = options; options = undefined; }
                    return Runtime.readdir(path).then(data => {
                        if (options && options.withFileTypes) {
                            callback(null, data);
                        }
                        else {
                            callback(null, data.map(item => item.name));
                        }
                    }).catch(err => callback(err));
                }

                fs.readFileSync = function (path, options) {
                    return Runtime.readFileSync(path, options);
                }

                // override net.Server: a listening socket is a cluster-wide socket (OneOS-V6 plan step 8).
                // The runtime claims the port for this program across the cluster, the program listens on
                // the OneOS loopback address, and every runtime proxies the port to it. Forms:
                //   listen(port[, host][, backlog][, callback]), listen({ port, ... }[, callback]),
                //   listen([callback]) (any free port). A host is ignored: the loopback address is used.
                //   listen({ port, oneosIndependent: true }) listens on the public interface itself, unproxied.
                // Other forms (a path, a handle) are local.
                const originalListen = net.Server.prototype.listen;
                const originalClose = net.Server.prototype.close;
                net.Server.prototype.listen = function (...args) {
                    const callback = typeof args[args.length - 1] === 'function' ? args[args.length - 1] : undefined;
                    let port, options = null;
                    if (typeof args[0] === 'number' || (typeof args[0] === 'string' && /^\d+$/.test(args[0]))) {
                        port = Number(args[0]);
                    }
                    else if (args[0] && typeof args[0] === 'object' && args[0].path === undefined && args[0].fd === undefined && args[0]._handle === undefined) {
                        options = args[0];
                        port = Number(options.port || 0);
                    }
                    else if (args.length === 0 || (args.length === 1 && callback)) {
                        port = 0;
                    }
                    else {
                        return originalListen.apply(this, args);
                    }

                    const server = this;
                    rpc.request('CreateServer', port, !!(options && options.oneosIndependent))
                        .then(result => {
                            server[PROPERTY_PREFIX + 'port'] = result.port;
                            const listenOptions = Object.assign({}, options || {}, { port: result.port, host: result.host });
                            delete listenOptions.oneosIndependent;
                            originalListen.call(server, listenOptions, callback);
                        }, err => {
                            const code = String(err.message).split(':')[0];
                            const error = new Error(`listen ${code}: ${err.message}`);
                            error.code = code;
                            error.syscall = 'listen';
                            error.port = port;
                            server.emit('error', error);
                        });
                    return this;
                }

                net.Server.prototype.close = function (callback) {
                    const port = this[PROPERTY_PREFIX + 'port'];
                    if (port !== undefined) {
                        delete this[PROPERTY_PREFIX + 'port'];
                        rpc.request('ReleaseServer', port).catch(() => { });
                    }
                    return originalClose.call(this, callback);
                }

                // augment process object
                let jsonStream = null;
                // lazily instantiate jsonStream, because we don't need it unless the user explicitly wants it
                Object.defineProperty(process.stdin, 'json', {
                    get: function () {
                        if (!jsonStream) {
                            jsonStream = new JsonStream(process.stdin);
                        }
                        else if (!jsonStream.input) {
                            jsonStream.setInputStream(process.stdin);
                        }
                        return jsonStream;
                    }
                });
                Object.defineProperty(process.stdout, 'json', {
                    get: function () {
                        if (!jsonStream) {
                            jsonStream = new JsonStream(null, process.stdout);
                        }
                        else if (!jsonStream.output) {
                            jsonStream.setOutputStream(process.stdout);
                        }
                        return jsonStream;
                    }
                });

                let messageStream = null;
                // lazily instantiate messageStream, in case we want to read segmented input (e.g., video)
                Object.defineProperty(process.stdin, 'segment', {
                    get: function () {
                        if (!messageStream) {
                            messageStream = new MessageStream(process.stdin);
                        }
                        else if (!messageStream.input) {
                            messageStream.setInputStream(process.stdin);
                        }
                        return messageStream;
                    }
                });
                Object.defineProperty(process.stdout, 'segment', {
                    get: function () {
                        if (!messageStream) {
                            messageStream = new MessageStream(null, process.stdout);
                        }
                        else if (!messageStream.output) {
                            messageStream.setOutputStream(process.stdout);
                        }
                        return messageStream;
                    }
                });

                process.uri = root.meta.uri;
                process.cwd = () => root.meta.cwd;  // this needs to be overwritten so that API such as path.resolve works as expected

                // replace localhost environment with oneos environment
                const oneosEnv = {};
                Object.keys(process.env).forEach(key => {
                    if (key.indexOf('ONEOS_') === 0) {
                        oneosEnv[key.slice(6)] = process.env[key];
                    }
                });
                process.env = oneosEnv;

                // attach oneos-specific API to the process object
                process.runtime = {
                    events: runtimeEventEmitter,
                    logIn: Runtime.logIn,
                    getRegistry: Runtime.getRegistry,
                    getRuntimes: Runtime.getRuntimes
                }

                resolve(rpc);
            });

            return connected;
        }
        else throw new Error('Runtime.connect was called more than once');
    },
    /* fs module */
    readFileSync: (path, opts) => {
        const encoding = typeof opts === 'string' ? opts : (opts && opts.encoding);
        if (encoding) return syncRpcRequest('ReadTextFile', path);
        return Buffer.from(syncRpcRequest('ReadFile', path), 'base64');
    },
    writeFileSync: (path, content, opts) => {
        const data = Buffer.isBuffer(content) ? content : Buffer.from(String(content));
        syncRpcRequest('WriteFile', path, data.toString('base64'));
    },
    readFile: async (path, encoding) => {
        await connected;
        let result;
        if (encoding === 'raw') {
            result = await rpc.request('ReadFile', path);
            result = Buffer.from(result, 'base64');
        }
        else {
            result = await rpc.request('ReadTextFile', path);
        }
        return result;
    },
    writeFile: async (path, content, encoding) => {
        await connected;
        let result;
        if (encoding === 'raw') {
            result = await rpc.request('WriteFile', path, content.toString('base64'));
        }
        else {
            result = await rpc.request('WriteTextFile', path, String(content));
        }
        return result;
    },
    appendFile: async (path, content) => {
        await connected;
        let result = await rpc.request('AppendTextFile', path, String(content));
        return result;
    },
    createReadStream: (path) => {
        let stream = new DirectPipe('fs', 'read');

        (async () => {
            await connected;
            try {
                let result = await rpc.request('CreateReadStream', path);
                stream.connect(result);
            }
            catch (err) {
                if (err.message === 'ENOENT') {
                    err.code = 'ENOENT';
                }
                stream.destroy(err);
            }
        })();

        // add metadata for migration
        stream[PROPERTY_PREFIX + 'data'] = () => ({
            init: 'require("fs").restoreReadStream',
            path: path,
            bytesRead: stream.bytesRead
        });

        return stream;
    },
    createWriteStream: (path) => {
        let stream = new DirectPipe('fs', 'write');

        (async () => {
            await connected;
            try {
                let result = await rpc.request('CreateWriteStream', path);
                stream.connect(result);
            }
            catch (err) {
                if (err.message === 'ENOENT') err.code = 'ENOENT';
                stream.destroy(err);
            }
        })();

        return stream;
    },
    fsStat: async (path) => {
        await connected;

        try {
            let result = await rpc.request('GetFileStats', path);


            // need to implement at least the following for fs.stat
            // (see serve-static module) https://github.com/expressjs/serve-static/blob/master/index.js
            if (result.type == 'directory') {
                return {
                    isDirectory: () => true,
                    isFile: () => false
                };
            }
            else {
                // our fake fs.Stats object must have mtime, ctime, ino, and size
                // (see etag module) https://github.com/jshttp/etag/blob/master/index.js
                return {
                    isDirectory: () => false,
                    isFile: () => true,
                    size: result.value.size,
                    mtime: new Date(),
                    ctime: new Date(),
                    ino: Math.random() // Inode doesn't exist in OneOS
                };
            }
        }
        catch (err) {
            if (err.message == "ENOENT") {
                let enoent = new Error(path + ' does not exist');
                enoent.code = "ENOENT";
                throw enoent;
            }
            else {
                throw err;
            }
        }
    },
    readdir: async (path) => {
        await connected;
        let result = await rpc.request('ReadDirectory', path);
        result = Object.entries(result.value).map(entry => ({
            name: entry[0],
            isFile: () => entry[1].type === 'file',
            isDirectory: () => entry[1].type === 'directory',
            isSocket: () => entry[1].type === 'socket'
        }));

        return result;
    },
    // the following are oneos/fs specific API used during migration
    restoreReadStream: (path, bytesRead) => {
        let stream = new DirectPipe('fs', 'read');
        stream.bytesRead = bytesRead;

        (async () => {
            await connected;
            try {
                let result = await rpc.request('RestoreReadStream', path, bytesRead);
                stream.connect(result);
            }
            catch (err) {
                if (err.message === 'ENOENT') err.code = 'ENOENT';
                stream.destroy(err);
            }
        })();

        // add metadata for migration
        stream[PROPERTY_PREFIX + 'data'] = () => ({
            init: 'require("fs").restoreReadStream',
            path: path,
            bytesRead: stream.bytesRead
        });

        return stream;
    },
    /* net module */
    createServer: (options, connectionListener) => net.createServer(options, connectionListener),
    /* io module */
    createVideoInputStream: (path) => {
        let stream = new DirectPipe('io', 'read', 'mjpeg');

        (async () => {
            await connected;
            try {
                let result = await rpc.request('CreateFfmpegStream', path);
                stream.connect(result);
            }
            catch (err) {
                if (err.message === 'ENOENT') err.code = 'ENOENT';
                stream.destroy(err);
            }
        })();

        return stream;
    },
    createKafkaInputStream: (kafkaServer, topic, options) => {
        let dataFormat = options && options.format ? options.format : 'raw';
        let batchSize = options && options.batchSize ? options.batchSize : 10000;
        let stream = new DirectPipe('io', 'read', dataFormat);

        (async () => {
            await connected;
            try {
                let result = await rpc.request('CreateKafkaInputStream', kafkaServer, topic, batchSize);
                stream.connect(result);
            }
            catch (err) {
                if (err.message === 'ENOENT') err.code = 'ENOENT';
                stream.destroy(err);
            }
        })();

        return stream;
    },
    rpcRequest: async (method, ...args) => {
        let result = await rpc.request('RuntimeMethod', method, ...args);
        return result;
    },
    createAgentTunnel: (sessionKey, agentUri, channelIn, channelOut) => new AgentTunnel(sessionKey, agentUri, channelIn, channelOut),
    createAgentMonitorStream: (agentUri) => {
        let stream = new DirectPipe('monitor', 'read', 'segment');

        (async () => {
            await connected;
            try {
                let result = await rpc.request('CreateAgentMonitorStream', agentUri);
                stream.connect(result);
            }
            catch (err) {
                stream.destroy(err);
            }
        })();

        return stream;
    },
    /* oneos-specific */
    logIn: async (username, password, clientId) => {
        let result = await rpc.request('LogIn', username, password, clientId);
        return result;
    },
    getRegistry: async () => {
        return await rpc.request('GetRegistry');
    },
    getRuntimes: async () => {
        return await rpc.request('GetRuntimes');
    }
}

module.exports = Runtime;