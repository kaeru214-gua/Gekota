(function () {
  'use strict';

  var params = new URLSearchParams(location.search);
  var room = String(params.get('room') || '');

  var video = document.getElementById('player');
  var chatList = document.getElementById('chat-list');
  var chatState = document.getElementById('chat-state');
  var loginState = document.getElementById('login-state');
  var stageState = document.getElementById('stage-state');
  var roomTitle = document.getElementById('room-title');
  var empty = document.getElementById('stage-empty');
  var emptySub = document.getElementById('empty-sub');
  var input = document.getElementById('chat-input');
  var sendButton = document.getElementById('send');
  var sendState = document.getElementById('send-state');
  var danmakuLayer = document.getElementById('danmaku-layer');
  var playButton = document.getElementById('play');
  var muteButton = document.getElementById('mute');
  var volume = document.getElementById('volume');
  var quality = document.getElementById('quality');

  var player = null;
  var retryTimer = null;
  var stopped = false;
  var lineCount = 0;
  var currentQn = 0;
  var flyEnabled = true;
  var autoScroll = true;
  var lastDmText = '';
  var lastDmAt = 0;
  var sendReadyAt = 0;
  var sendQueue = [];
  var sending = false;
  var autoMode = true;
  var connectedAt = 0;
  var stallHits = [];
  var lastDowngradeAt = 0;

  var dm = { size: 22, opacity: 100, speed: 9, area: 'full', color: '#ffffff', stroke: 1 };
  try {
    var savedDm = JSON.parse(localStorage.getItem('gekota_dm') || 'null');
    if (savedDm && typeof savedDm === 'object') {
      dm.size = Number(savedDm.size) || dm.size;
      dm.opacity = Number(savedDm.opacity) || dm.opacity;
      dm.speed = Number(savedDm.speed) || dm.speed;
      dm.area = savedDm.area || dm.area;
      dm.color = savedDm.color || dm.color;
      dm.stroke = savedDm.stroke === 0 ? 0 : 1;
    }
  } catch (e) { }

  function applyDmSettings() {
    danmakuLayer.style.setProperty('--dm-size', dm.size + 'px');
    danmakuLayer.style.setProperty('--dm-opacity', String(dm.opacity / 100));
    danmakuLayer.style.setProperty('--dm-color', dm.color);
    danmakuLayer.style.setProperty('--dm-shadow',
      dm.stroke ? '0 1px 2px #000, 0 0 6px #000' : 'none');
    try { localStorage.setItem('gekota_dm', JSON.stringify(dm)); } catch (e) { }
  }
  applyDmSettings();

  function setStageState(text) {
    stageState.textContent = text || '';
  }

  function showError(text) {
    empty.hidden = false;
    emptySub.textContent = text || '';
    setStageState('未连接');
  }

  function hideError() {
    empty.hidden = true;
  }

  function addChat(uname, text, sys) {
    var line = document.createElement('div');
    line.className = 'chat-line' + (sys ? ' chat-sys' : '');
    if (!sys && uname) {
      var u = document.createElement('span');
      u.className = 'chat-uname';
      u.textContent = uname + '：';
      line.appendChild(u);
    }
    line.appendChild(document.createTextNode(text || ''));
    var stick = autoScroll && chatList.scrollTop + chatList.clientHeight >= chatList.scrollHeight - 30;
    chatList.appendChild(line);
    lineCount++;
    while (lineCount > 120 && chatList.firstChild) {
      chatList.removeChild(chatList.firstChild);
      lineCount--;
    }
    if (stick) chatList.scrollTop = chatList.scrollHeight;
  }

  function flyDanmaku(text) {
    if (!flyEnabled || !text) return;
    var el = document.createElement('div');
    el.className = 'dm';
    el.textContent = text;
    var top;
    if (dm.area === 'top') top = 6 + Math.random() * 30;
    else if (dm.area === 'bottom') top = 38 + Math.random() * 32;
    else top = 8 + Math.random() * 62;
    el.style.top = top + '%';
    el.style.animationDuration = (dm.speed + Math.random() * 2) + 's';
    el.addEventListener('animationend', function () { el.remove(); });
    danmakuLayer.appendChild(el);
  }

  function destroyPlayer() {
    if (retryTimer) {
      clearTimeout(retryTimer);
      retryTimer = null;
    }
    if (player) {
      try { player.pause(); player.unload(); player.detachMediaElement(); } catch (e) { }
      player = null;
    }
    try { video.removeAttribute('src'); video.load(); } catch (e) { }
  }

  function connect() {
    destroyPlayer();
    hideError();
    if (!room) {
      showError('没有房间号');
      return;
    }
    if (typeof mpegts === 'undefined') {
      showError('mpegts.js 没有加载');
      return;
    }

    setStageState('连接中…');
    connectedAt = Date.now();
    stallHits = [];
    player = mpegts.createPlayer({
      type: 'flv',
      isLive: true,
      url: '/stream?room=' + encodeURIComponent(room) +
           (currentQn > 0 ? '&qn=' + currentQn : '') + '&t=' + Date.now(),
      enableStashBuffer: false,
      liveBufferLatencyChasing: true
    });

    player.attachMediaElement(video);
    player.load();
    player.on(mpegts.Events.ERROR, function () {
      destroyPlayer();
      showError('直播连接中断，正在重试…');
      if (!stopped) {
        retryTimer = setTimeout(connect, 6000);
      }
    });

    try {
      var p = player.play();
      if (p && typeof p.catch === 'function') p.catch(function () { });
    } catch (e) { }
  }

  video.addEventListener('playing', function () {
    hideError();
    setStageState('直播中');
    playButton.textContent = '❚❚';
    stallHits = [];
    fetch('/api/info').then(function (r) { return r.json(); }).then(function (d) {
      if (d && d.qualities && d.qualities.length) setQualities(d.qualities, d.qn);
    }).catch(function () { });
  });
  video.addEventListener('pause', function () { playButton.textContent = '▶'; });
  video.addEventListener('waiting', function () {
    setStageState('缓冲中…');
    maybeDowngrade();
  });
  video.addEventListener('stalled', maybeDowngrade);
  video.addEventListener('error', function () {
    showError('视频播放失败，正在重试…');
    if (!stopped && !retryTimer) retryTimer = setTimeout(connect, 6000);
  });

  playButton.addEventListener('click', function () {
    if (video.paused) {
      var p = video.play();
      if (p && typeof p.catch === 'function') p.catch(function () { });
    } else {
      video.pause();
    }
  });

  muteButton.addEventListener('click', function () {
    video.muted = !video.muted;
    muteButton.textContent = video.muted ? '🔇' : '🔊';
  });

  volume.addEventListener('input', function () {
    video.volume = Number(volume.value);
    video.muted = video.volume === 0;
    muteButton.textContent = video.muted ? '🔇' : '🔊';
  });

  document.getElementById('retry').addEventListener('click', connect);

  function send() {
    var text = (input.value || '').trim();
    if (!text) return;
    if (text.length > 30) {
      setSendState('弹幕最长 30 个字', true);
      return;
    }
    input.value = '';
    sendQueue.push(text);
    setSendState('已加入发送队列', false);
    processQueue();
  }

  function delay(ms) {
    return new Promise(function (resolve) { setTimeout(resolve, ms); });
  }

  function postOne(text, attempt) {
    return fetch('/api/send', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text: text })
    }).then(function (r) { return r.json(); }).then(function (d) {
      if (d && d.ok) return { ok: true };

      var message = (d && d.message) || '发送失败';
      var retryable = d && (d.code === 429 || /太快|稍等|频繁|限流/.test(message));
      if (retryable && attempt < 2) {
        setSendState('发送受限，2 秒后重试…', false);
        return delay(2600).then(function () { return postOne(text, attempt + 1); });
      }
      return { ok: false, message: message };
    }).catch(function () {
      if (attempt < 2) {
        setSendState('网络失败，2 秒后重试…', false);
        return delay(2000).then(function () { return postOne(text, attempt + 1); });
      }
      return { ok: false, message: '发送失败，请检查本机服务' };
    });
  }

  function processQueue() {
    if (sending || !sendQueue.length) {
      if (!sendQueue.length) sendButton.disabled = false;
      return;
    }

    sending = true;
    sendButton.disabled = true;
    var text = sendQueue[0];
    var wait = Math.max(0, sendReadyAt - Date.now());
    setTimeout(function () {
      sendReadyAt = Date.now() + 2500;
      processNext(text);
    }, wait);
  }

  function processNext(text) {
    postOne(text, 0).then(function (result) {
      sendQueue.shift();
      if (result.ok) {
        lastDmText = text;
        lastDmAt = Date.now();
        addChat('我', text, false);
        flyDanmaku(text);
        setSendState('已发送 ✓', false, true);
      } else {
        setSendState(result.message || '发送失败', true);
      }
      sending = false;
      if (sendQueue.length) setTimeout(processQueue, 300);
      else sendButton.disabled = false;
    });
  }

  function setSendState(text, error, ok) {
    sendState.textContent = text || '';
    sendState.className = error ? 'err' : (ok ? 'ok' : '');
  }

  sendButton.addEventListener('click', send);
  input.addEventListener('keydown', function (e) {
    if (e.key === 'Enter') send();
  });

  quality.addEventListener('change', function () {
    var qn = Number(quality.value) || 0;
    autoMode = qn === 0;
    currentQn = qn;
    connect();
  });

  function maybeDowngrade() {
    if (!autoMode || !quality.options.length) return;
    var now = Date.now();
    if (now - connectedAt < 12000 || now - lastDowngradeAt < 30000) return;

    stallHits = stallHits.filter(function (t) { return now - t < 30000; });
    stallHits.push(now);
    if (stallHits.length < 2) return;

    var idx = quality.selectedIndex;
    if (idx < 0 || idx >= quality.options.length - 1) return;
    var next = quality.options[idx + 1];
    if (!next || next.value === '0') return;

    currentQn = Number(next.value) || 0;
    lastDowngradeAt = now;
    stallHits = [];
    quality.value = next.value;
    setStageState('网络卡顿，已自动降到 ' + next.textContent);
    connect();
  }

  document.getElementById('toggle-fly').addEventListener('click', function () {
    flyEnabled = !flyEnabled;
    this.classList.toggle('on', flyEnabled);
    if (!flyEnabled) danmakuLayer.innerHTML = '';
  });

  document.getElementById('toggle-scroll').addEventListener('click', function () {
    autoScroll = !autoScroll;
    this.classList.toggle('on', autoScroll);
    if (autoScroll) chatList.scrollTop = chatList.scrollHeight;
  });

  document.getElementById('clear-chat').addEventListener('click', function () {
    chatList.innerHTML = '';
    lineCount = 0;
    danmakuLayer.innerHTML = '';
  });

  function setQualities(list, qn) {
    quality.innerHTML = '';
    var auto = document.createElement('option');
    auto.value = '0';
    auto.textContent = '自动';
    quality.appendChild(auto);

    (list || []).forEach(function (q) {
      var opt = document.createElement('option');
      opt.value = String(q.qn);
      opt.textContent = q.desc || ('qn ' + q.qn);
      quality.appendChild(opt);
    });

    if (qn > 0 && (list || []).some(function (q) { return q.qn === qn; })) {
      currentQn = qn;
      quality.value = String(qn);
    } else if (!qn) {
      currentQn = 0;
      quality.value = '0';
    } else if (quality.options.length > 1) {
      currentQn = Number(quality.options[1].value) || 0;
      quality.value = quality.options[1].value;
    }
  }

  function bindDmSettings() {
    var panel = document.getElementById('dm-panel');
    var size = document.getElementById('dm-size');
    var opacity = document.getElementById('dm-opacity');
    var speed = document.getElementById('dm-speed');
    var area = document.getElementById('dm-area');
    var color = document.getElementById('dm-color');
    var stroke = document.getElementById('dm-stroke');

    size.value = String(dm.size);
    opacity.value = String(dm.opacity);
    speed.value = String(dm.speed);
    area.value = dm.area;
    color.value = dm.color;
    stroke.value = String(dm.stroke);

    document.getElementById('dm-settings').addEventListener('click', function () {
      panel.hidden = !panel.hidden;
      this.classList.toggle('on', !panel.hidden);
    });

    function changed() {
      dm.size = Number(size.value) || 22;
      dm.opacity = Number(opacity.value) || 100;
      dm.speed = Number(speed.value) || 9;
      dm.area = area.value || 'full';
      dm.color = color.value || '#ffffff';
      dm.stroke = Number(stroke.value) === 0 ? 0 : 1;
      applyDmSettings();
    }

    size.addEventListener('input', changed);
    opacity.addEventListener('input', changed);
    speed.addEventListener('input', changed);
    area.addEventListener('change', changed);
    color.addEventListener('change', changed);
    stroke.addEventListener('change', changed);

    document.querySelectorAll('#dm-panel [data-preset]').forEach(function (button) {
      button.addEventListener('click', function () {
        var preset = button.getAttribute('data-preset');
        if (preset === 'nofly') {
          flyEnabled = false;
          document.getElementById('toggle-fly').classList.remove('on');
          setSendState('飞屏已关闭', false);
          return;
        }

        flyEnabled = true;
        document.getElementById('toggle-fly').classList.add('on');
        if (preset === 'simple') {
          dm.size = 20; dm.opacity = 80; dm.speed = 11; dm.area = 'top';
          dm.color = '#ffffff'; dm.stroke = 0;
        } else if (preset === 'bright') {
          dm.size = 26; dm.opacity = 100; dm.speed = 8; dm.area = 'full';
          dm.color = '#ffe066'; dm.stroke = 1;
        }

        size.value = String(dm.size);
        opacity.value = String(dm.opacity);
        speed.value = String(dm.speed);
        area.value = dm.area;
        color.value = dm.color;
        stroke.value = String(dm.stroke);
        applyDmSettings();
        setSendState('已应用弹幕预设', false, true);
      });
    });
  }
  bindDmSettings();

  // C# 桥：弹幕 / 状态 / 房间信息
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', function (e) {
      var m = e.data || {};
      if (m.type === 'danmaku') {
        if (m.text === lastDmText && Date.now() - lastDmAt < 1200) return;
        lastDmText = m.text || '';
        lastDmAt = Date.now();
        addChat(m.uname, m.text, false);
        flyDanmaku(m.text);
        chatState.textContent = '实时';
      } else if (m.type === 'state') {
        chatState.textContent = m.text || '';
        if (m.text) addChat('', m.text, true);
      } else if (m.type === 'info') {
        if (m.title) roomTitle.textContent = m.title;
        if (typeof m.logged === 'boolean') {
          loginState.textContent = m.logged ? '已登录' : '未登录';
        }
      }
    });
  }

  fetch('/api/info').then(function (r) { return r.json(); }).then(function (d) {
    if (!d) return;
    if (d.title) roomTitle.textContent = d.title;
    if (d.room) room = String(d.room);
    loginState.textContent = d.logged ? '已登录' : '未登录';
    setQualities(d.qualities, d.qn);
  }).catch(function () { });

  document.addEventListener('keydown', function (e) {
    var tag = (e.target && e.target.tagName) || '';
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
    if (e.code === 'Space') {
      e.preventDefault();
      if (video.paused) {
        var p = video.play();
        if (p && typeof p.catch === 'function') p.catch(function () { });
      } else {
        video.pause();
      }
    } else if (e.key === 'f' || e.key === 'F') {
      if (document.fullscreenElement) document.exitFullscreen();
      else document.documentElement.requestFullscreen().catch(function () { });
    } else if (e.key === 'm' || e.key === 'M') {
      video.muted = !video.muted;
      muteButton.textContent = video.muted ? '🔇' : '🔊';
    }
  });

  document.getElementById('back').addEventListener('click', function () {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage({ type: 'back' });
    }
  });

  document.querySelectorAll('#stage-actions [data-action]').forEach(function (button) {
    button.addEventListener('click', function (e) {
      e.stopPropagation();
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage({
          type: 'command',
          action: button.getAttribute('data-action')
        });
      }
    });
  });

  connect();
})();
