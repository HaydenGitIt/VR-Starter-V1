// Browser test client for /conversation - the same protocol Unity uses.

const $ = (id) => document.getElementById(id);

let socket = null;
let audio = null;       // AudioContext, created on first click (browser rule).
let nextStart = 0;      // When the next audio chunk should start, for gapless play.
let playbackRate = 24000;
let counts = {turn: 0, strikes: 0, progress: 0};
let limits = {};

/**
 * Appends a line to the log.
 * @param {string} cls CSS class for the line.
 * @param {string} text Line content.
 * @return {!HTMLElement} The new line.
 */
function log(cls, text) {
  const div = document.createElement('div');
  div.className = cls;
  div.textContent = text;
  $('log').append(div);
  $('log').scrollTop = $('log').scrollHeight;
  return div;
}

/** Shows the turn counters against the scenario limits. */
function showCounters() {
  $('counters').textContent =
      `Turn ${counts.turn}/${limits.max_turns} · ` +
      `strikes ${counts.strikes}/${limits.strikes_allowed} · ` +
      `progress ${counts.progress}/${limits.progress_to_resolve}`;
}

/** Polls /health until the engines are warm, then connects. */
async function waitForServer() {
  try {
    const health = await (await fetch('/health')).json();
    if (health.ready) {
      playbackRate = health.playback.sample_rate;
      connect();
      return;
    }
    $('status').textContent = 'Server is warming up its engines…';
  } catch (e) {
    $('status').textContent = 'Server unreachable. Retrying…';
  }
  setTimeout(waitForServer, 2000);
}

/** Opens the WebSocket. */
function connect() {
  if (socket) socket.close();
  socket = new WebSocket(`ws://${location.host}/conversation`);
  socket.binaryType = 'arraybuffer';
  socket.onopen = () => $('status').textContent = 'Connected.';
  socket.onclose = () => $('status').textContent = 'Disconnected.';
  socket.onmessage = (event) => {
    if (event.data instanceof ArrayBuffer) {
      playChunk(new Float32Array(event.data));
    } else {
      handleMessage(JSON.parse(event.data));
    }
  };
}

/**
 * Renders one JSON message from the server.
 * @param {!Object} msg The parsed message.
 */
function handleMessage(msg) {
  switch (msg.type) {
    case 'ready':
      playbackRate = msg.playback_sample_rate;
      limits = msg;
      counts = {turn: 0, strikes: 0, progress: 0};
      showCounters();
      log('meta', `ready · playback ${msg.playback_sample_rate} Hz`);
      break;
    case 'transcript':
      log('trainee', `Heard: ${msg.text}`);
      break;
    case 'tags':
      log('meta', `verdict ${msg.verdict} · emotion ${msg.emotion}`);
      break;
    case 'reply':
      log('parent', `Parent: ${msg.text}`);
      break;
    case 'turn_complete':
      counts.turn = msg.turn;
      if (msg.verdict === 'neutral') counts.strikes++;
      if (msg.verdict === 'deescalated') counts.progress++;
      showCounters();
      log('meta', `turn ${msg.turn} complete · ${msg.latency}s`);
      break;
    case 'ending':
      log(msg.outcome === 'resolved' ? 'good' : 'bad', `ENDING: ${msg.outcome}`);
      break;
    case 'turn_end':
      log('meta', `turn end${msg.ended ? ' · scenario over' : ''}`);
      break;
    case 'reset_complete':
      counts = {turn: 0, strikes: 0, progress: 0};
      showCounters();
      log('meta', 'reset complete');
      break;
    case 'summary': {
      const pre = document.createElement('pre');
      pre.textContent = JSON.stringify(msg, null, 2);
      log('meta', 'summary:').append(pre);
      break;
    }
    case 'error':
      log('bad', `Error: ${msg.message}`);
      break;
    default:
      log('meta', JSON.stringify(msg));
  }
}

/**
 * Queues one chunk of float32 PCM so chunks play back to back.
 * @param {!Float32Array} samples Mono audio at the playback rate.
 */
function playChunk(samples) {
  if (!audio || samples.length === 0) return;
  const buffer = audio.createBuffer(1, samples.length, playbackRate);
  buffer.copyToChannel(samples, 0);
  const source = audio.createBufferSource();
  source.buffer = buffer;
  source.connect(audio.destination);
  nextStart = Math.max(nextStart, audio.currentTime);
  source.start(nextStart);
  nextStart += buffer.duration;
}

/**
 * Sends a command, creating the AudioContext on first use.
 * @param {string} type Command type.
 * @param {string=} content Optional command content.
 */
function send(type, content) {
  audio = audio || new AudioContext();
  if (!socket || socket.readyState !== WebSocket.OPEN) {
    log('bad', 'Not connected.');
    return;
  }
  socket.send(JSON.stringify(content === undefined ? {type} : {type, content}));
}

$('begin').onclick = () => send('begin');
$('reset').onclick = () => send('reset');
$('summary').onclick = () => send('summary');
$('connect').onclick = () => waitForServer();
$('form').onsubmit = (event) => {
  event.preventDefault();
  const line = $('line').value.trim();
  if (!line) return;
  log('trainee', `Trainee: ${line}`);
  send('text', line);
  $('line').value = '';
};

waitForServer();
