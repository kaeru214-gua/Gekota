(function () {
  'use strict';

  var wall = document.getElementById('wall');
  var empty = document.getElementById('empty');
  var players = {};
  var rooms = [];
  var signature = '';
  var pollTimer = null;

  function esc(value) {
    return String(value == null ? '' : value)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function openRoom(roomId) {
    var id = Number(roomId) || 0;
    if (!id) return;
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage({ type: 'open', room: id });
    } else {
      location.href = '/index.html?room=' + encodeURIComponent(id);
    }
  }

  function teardown() {
    Object.keys(players).forEach(function (key) {
      var item = players[key];
      try { item.player.destroy(); } catch (e) { }
      try { item.video.remove(); } catch (e) { }
    });
    players = {};
  }

  function makeCell(room) {
    var cell = document.createElement('div');
    cell.className = 'cell ' + (room.live ? 'live' : 'offline');
    cell.setAttribute('data-room', room.room);
    cell.addEventListener('click', function () { openRoom(room.room); });

    if (room.cover && !room.live) {
      var img = document.createElement('img');
      img.className = 'cover';
      img.alt = '';
      img.src = '/cover?url=' + encodeURIComponent(room.cover);
      img.onerror = function () { img.style.display = 'none'; };
      cell.appendChild(img);
    } else {
      var fallback = document.createElement('div');
      fallback.className = 'cover-fallback';
      fallback.innerHTML = '<b>' + esc((room.name || '?').slice(0, 1)) + '</b>';
      cell.appendChild(fallback);
    }

    var shade = document.createElement('div');
    shade.className = 'shade';
    cell.appendChild(shade);

    var name = document.createElement('div');
    name.className = 'name';
    name.textContent = room.name || ('房间 ' + room.room);
    cell.appendChild(name);

    var foot = document.createElement('div');
    foot.className = 'foot';
    var dot = document.createElement('span');
    dot.className = 'dot';
    var state = document.createElement('span');
    state.className = 'state';
    state.textContent = room.live ? '连接中…' : '未开播';
    var open = document.createElement('span');
    open.className = 'open';
    open.textContent = '进入播放 ›';
    foot.appendChild(dot);
    foot.appendChild(state);
    foot.appendChild(open);
    cell.appendChild(foot);

    if (room.live) attachLive(cell, room, state);
    return cell;
  }

  function attachLive(cell, room, state) {
    if (typeof mpegts === 'undefined') {
      state.textContent = '播放库缺失';
      return;
    }

    var cover = cell.querySelector('.cover');
    if (cover) cover.style.display = 'none';

    var video = document.createElement('video');
    video.muted = false;
    video.volume = 0.8;
    video.autoplay = true;
    video.playsInline = true;
    video.removeAttribute('muted');
    cell.appendChild(video);

    var foot = cell.querySelector('.foot');
    if (foot) {
      var mute = document.createElement('button');
      mute.type = 'button';
      mute.className = 'cell-mute';
      mute.title = '静音这一路';
      mute.setAttribute('aria-label', '静音这一路');
      mute.innerHTML =
        '<svg class="sound-on" viewBox="0 0 24 24" aria-hidden="true">' +
        '<path d="M4 9.5h3.2L12 5.7v12.6L7.2 14.5H4z"/>' +
        '<path d="M15.5 8.3a5 5 0 0 1 0 7.4M18 5.8a8.5 8.5 0 0 1 0 12.4"/></svg>' +
        '<svg class="sound-off" viewBox="0 0 24 24" aria-hidden="true">' +
        '<path d="M4 9.5h3.2L12 5.7v12.6L7.2 14.5H4z"/>' +
        '<path d="m16 9.5 5 5m0-5-5 5"/></svg>';
      mute.addEventListener('click', function (e) {
        e.stopPropagation();
        video.muted = !video.muted;
        mute.classList.toggle('muted', video.muted);
        mute.title = video.muted ? '取消静音这一路' : '静音这一路';
        mute.setAttribute('aria-label', mute.title);
        if (!video.muted) {
          try {
            var p = video.play();
            if (p && p.catch) p.catch(function () { });
          } catch (err) { }
        }
      });
      foot.appendChild(mute);
    }

    var attempts = 0;
    var maxAttempts = 4;
    var destroyed = false;

    function connect() {
      if (destroyed) return;
      var player = mpegts.createPlayer({
        type: 'flv',
        isLive: true,
        url: '/stream?room=' + encodeURIComponent(room.room) + '&qn=250&t=' + Date.now(),
        enableStashBuffer: true,
        stashInitialSize: 256,
        liveBufferLatencyChasing: true,
        liveBufferLatencyMaxLatency: 3,
        liveBufferLatencyMinRemain: 1
      });

      var item = { player: player, video: video };
      if (players[room.room]) {
        try { players[room.room].player.destroy(); } catch (e) { }
      }
      players[room.room] = item;

      player.attachMediaElement(video);
      player.load();
      try {
        var p = player.play();
        if (p && p.catch) p.catch(function () { });
      } catch (e) { }

      video.addEventListener('playing', function () {
        attempts = 0;
        state.textContent = '直播中';
      }, { once: true });

      player.on(mpegts.Events.ERROR, function () {
        try { player.destroy(); } catch (e) { }
        if (players[room.room] === item) delete players[room.room];
        if (destroyed) return;
        attempts++;
        if (attempts <= maxAttempts) {
          state.textContent = '重连中…（' + attempts + '/' + maxAttempts + '）';
          setTimeout(connect, 3000);
        } else {
          state.textContent = '连接失败，点开重试';
        }
      });
    }

    cell.__destroyLive = function () {
      destroyed = true;
      if (players[room.room]) {
        try { players[room.room].player.destroy(); } catch (e) { }
        delete players[room.room];
      }
    };
    connect();
  }

  function teardownCells() {
    Array.prototype.forEach.call(wall.querySelectorAll('.cell'), function (cell) {
      if (cell.__destroyLive) try { cell.__destroyLive(); } catch (e) { }
    });
    Object.keys(players).forEach(function (key) {
      try { players[key].player.destroy(); } catch (e) { }
    });
    players = {};
    wall.innerHTML = '';
  }

  function render(list) {
    teardownCells();
    rooms = (list || []).slice().sort(function (a, b) {
      return (b.live ? 1 : 0) - (a.live ? 1 : 0);
    });
    empty.hidden = rooms.length > 0;
    if (!rooms.length) return;

    var live = rooms.filter(function (r) { return r.live; });
    var offline = rooms.filter(function (r) { return !r.live; });

    // 全部未开播：按数量切换 1 / 2 / 3 / 4 / 9 宫格，最多 9 个。
    if (!live.length) {
      var shown = rooms.slice(0, 9);
      var cols = shown.length <= 3 ? shown.length : (shown.length === 4 ? 2 : 3);
      var rows = shown.length <= 3 ? 1 : (shown.length === 4 ? 2 : 3);
      wall.className = "offline-wall";
      wall.style.gridTemplateColumns = "repeat(" + cols + ", minmax(0, 1fr))";
      wall.style.gridTemplateRows = "repeat(" + rows + ", minmax(0, 1fr))";
      shown.forEach(function (room) { wall.appendChild(makeCell(room)); });
      return;
    }

    if (live.length > 4) {
      wall.className = "grid";
      rooms.forEach(function (room) { wall.appendChild(makeCell(room)); });
      return;
    }

    wall.className = "wall";
    var leftCol = document.createElement("div");
    leftCol.className = "live-grid live-count-" + live.length;
    live.forEach(function (room) { leftCol.appendChild(makeCell(room)); });
    if (live.length === 1) {
      leftCol.style.gridTemplateColumns = "1fr";
      leftCol.style.gridTemplateRows = "1fr";
    } else if (live.length === 2) {
      leftCol.style.gridTemplateColumns = "1fr";
      leftCol.style.gridTemplateRows = "repeat(2, minmax(0, 1fr))";
    }
    wall.appendChild(leftCol);

    if (offline.length) {
      var rightCol = document.createElement("div");
      rightCol.className = "offline-col";
      offline.forEach(function (room) { rightCol.appendChild(makeCell(room)); });
      wall.appendChild(rightCol);
      leftCol.style.flex = "1 1 auto";
      rightCol.style.flex = "0 0 22%";
    }
  }

  function roomSignature(list) {
    return (list || []).map(function (r) {
      return r.room + ':' + (r.live ? '1' : '0') + ':' + (r.name || '');
    }).join('|');
  }

  function refresh(force) {
    fetch('/api/monitor/rooms', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (data) {
        var list = (data && data.rooms) || [];
        var sig = roomSignature(list);
        if (!force && sig === signature) return;
        signature = sig;
        render(list);
      })
      .catch(function () {
        if (!rooms.length) empty.hidden = false;
      });
  }

  refresh(true);
  pollTimer = setInterval(function () { refresh(false); }, 3000);
  window.addEventListener('beforeunload', function () {
    clearInterval(pollTimer);
    teardown();
  });
})();
