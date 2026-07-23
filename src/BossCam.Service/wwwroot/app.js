(() => {
  const LS_ORDER = "bosscam.viewOrder";
  const LS_LAYOUT = "bosscam.viewLayout";

  const state = {
    devices: [],
    selectedId: null,
    order: [], // device ids in display order
    layout: Number(localStorage.getItem(LS_LAYOUT) || 4),
    liveTimer: null,
    dirty: {},
    netPayload: null,
    imagePayload: null,
    streamPayload: null,
    storage: {
      continuousRecordings: "",
      highlights: "",
      snapshots: "",
    },
  };

  const $ = (id) => document.getElementById(id);
  const toast = (msg, ok = true) => {
    const el = $("toast");
    el.textContent = msg;
    el.className = `toast ${ok ? "ok" : "bad"}`;
    clearTimeout(toast._t);
    toast._t = setTimeout(() => el.classList.add("hidden"), 4500);
  };

  const esc = (s) =>
    String(s ?? "")
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;")
      .replaceAll('"', "&quot;");

  async function req(path, opts = {}) {
    const res = await fetch(path, {
      headers: { "Content-Type": "application/json", ...(opts.headers || {}) },
      ...opts,
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error(text || `${res.status} ${res.statusText}`);
    }
    if (res.status === 204) return null;
    const ct = res.headers.get("content-type") || "";
    if (ct.includes("application/json")) return res.json();
    return res.text();
  }

  function selected() {
    return state.devices.find((d) => d.id === state.selectedId) || null;
  }

  function deviceById(id) {
    return state.devices.find((d) => d.id === id) || null;
  }

  function labelOf(d) {
    return d.displayName || d.name || d.ipAddress || d.id;
  }

  function syncOrder() {
    const ids = state.devices.map((d) => d.id);
    let saved = [];
    try {
      saved = JSON.parse(localStorage.getItem(LS_ORDER) || "[]");
    } catch {
      saved = [];
    }
    const next = [];
    saved.forEach((id) => {
      if (ids.includes(id) && !next.includes(id)) next.push(id);
    });
    ids.forEach((id) => {
      if (!next.includes(id)) next.push(id);
    });
    state.order = next;
    localStorage.setItem(LS_ORDER, JSON.stringify(state.order));
  }

  function renderDevices() {
    const list = $("deviceList");
    list.innerHTML = "";
    $("camCount").textContent = `(${state.devices.length})`;
    state.devices.forEach((d) => {
      const li = document.createElement("li");
      if (d.id === state.selectedId) li.classList.add("active");
      li.innerHTML = `<div class="name">${esc(labelOf(d))}</div>
        <div class="sub">${esc(d.ipAddress || "—")} · ${esc(d.hardwareModel || d.deviceType || "camera")}</div>`;
      li.onclick = () => selectDevice(d.id);
      list.appendChild(li);
    });
  }

  function setLayout(n) {
    state.layout = n;
    localStorage.setItem(LS_LAYOUT, String(n));
    document.querySelectorAll("#layoutBtns button").forEach((b) => {
      b.classList.toggle("active", Number(b.dataset.layout) === n);
    });
    const grid = $("viewGrid");
    grid.className = `view-grid layout-${n}`;
    renderViewGrid();
  }

  function snapUrl(deviceId) {
    return `/api/devices/${deviceId}/snapshot?t=${Date.now()}`;
  }

  function renderViewGrid() {
    const grid = $("viewGrid");
    const n = state.layout;
    const ordered = state.order.map(deviceById).filter(Boolean);
    grid.innerHTML = "";

    for (let i = 0; i < n; i++) {
      const d = ordered[i];
      const tile = document.createElement("div");
      if (!d) {
        tile.className = "view-tile empty";
        tile.textContent = `Slot ${i + 1} — add/register a camera`;
        grid.appendChild(tile);
        continue;
      }

      tile.className = "view-tile" + (d.id === state.selectedId ? " selected" : "");
      tile.draggable = true;
      tile.dataset.deviceId = d.id;
      tile.innerHTML = `
        <div class="view-tile-bar">
          <div>
            <strong>${esc(labelOf(d))}</strong>
            <div class="sub">${esc(d.ipAddress || "")} · ${esc(d.hardwareModel || "")}</div>
          </div>
          <span class="sub">#${i + 1}</span>
        </div>
        <div class="view-tile-media">
          <img alt="${esc(labelOf(d))}" data-live="${d.id}" class="live-mjpeg" decoding="async" />
          <div class="fail" data-fail="${d.id}" style="display:none">Connecting live stream…</div>
        </div>
        <div class="view-tile-actions">
          <button type="button" data-act="select">Select</button>
          <button type="button" data-act="snap">Snapshot</button>
          <button type="button" data-act="rec">Record</button>
        </div>`;

      tile.addEventListener("dragstart", (e) => {
        tile.classList.add("dragging");
        e.dataTransfer.setData("text/plain", d.id);
        e.dataTransfer.effectAllowed = "move";
      });
      tile.addEventListener("dragend", () => tile.classList.remove("dragging"));
      tile.addEventListener("dragover", (e) => {
        e.preventDefault();
        tile.classList.add("drag-over");
      });
      tile.addEventListener("dragleave", () => tile.classList.remove("drag-over"));
      tile.addEventListener("drop", (e) => {
        e.preventDefault();
        tile.classList.remove("drag-over");
        const fromId = e.dataTransfer.getData("text/plain");
        const toId = d.id;
        if (!fromId || fromId === toId) return;
        const from = state.order.indexOf(fromId);
        const to = state.order.indexOf(toId);
        if (from < 0 || to < 0) return;
        const next = state.order.slice();
        next.splice(from, 1);
        next.splice(to, 0, fromId);
        state.order = next;
        localStorage.setItem(LS_ORDER, JSON.stringify(state.order));
        renderViewGrid();
        scheduleLiveRefresh();
      });

      tile.querySelector('[data-act="select"]').onclick = (e) => {
        e.stopPropagation();
        selectDevice(d.id);
      };
      tile.querySelector('[data-act="snap"]').onclick = async (e) => {
        e.stopPropagation();
        await saveSnapshot(d.id);
      };
      tile.querySelector('[data-act="rec"]').onclick = async (e) => {
        e.stopPropagation();
        await startRecording(d.id);
      };
      tile.onclick = () => selectDevice(d.id);

      grid.appendChild(tile);
    }

    refreshLiveImages();
  }

  function liveStreamUrl(deviceId) {
    // Continuous RTSP→MJPEG (or snapShot pump fallback). quality=sub for multi-view fluidity.
    const q = ($("streamQuality") && $("streamQuality").value) || "sub";
    return `/api/devices/${deviceId}/live.mjpeg?quality=${encodeURIComponent(q)}&t=${Date.now()}`;
  }

  function attachLiveStreams() {
    document.querySelectorAll("img[data-live]").forEach((img) => {
      const id = img.getAttribute("data-live");
      const fail = document.querySelector(`[data-fail="${id}"]`);
      // Force reconnect if already set
      img.onload = () => {
        if (fail) fail.style.display = "none";
        img.style.display = "block";
      };
      img.onerror = () => {
        if (fail) {
          fail.style.display = "grid";
          fail.textContent = "Stream failed — retrying…";
        }
        // Retry after short delay (camera busy / ffmpeg spin-up)
        clearTimeout(img._retry);
        img._retry = setTimeout(() => {
          img.src = liveStreamUrl(id);
        }, 1500);
      };
      img.src = liveStreamUrl(id);
    });
  }

  function refreshLiveImages() {
    // Reconnect all MJPEG streams (used after layout/order change)
    attachLiveStreams();
  }

  function scheduleLiveRefresh() {
    // Continuous MJPEG does not need interval polling.
    if (state.liveTimer) {
      clearInterval(state.liveTimer);
      state.liveTimer = null;
    }
    // Optional: only refresh the single overview snap if that tab is open
    if (state.liveTimer) return;
    state.liveTimer = setInterval(() => {
      if ($("tab-overview")?.classList.contains("active") && state.selectedId) {
        loadSnapshotPreview();
      }
    }, 2000);
  }

  async function loadDevices() {
    state.devices = (await req("/api/devices")) || [];
    syncOrder();
    renderDevices();
    renderViewGrid();
    if (state.selectedId && !selected()) state.selectedId = null;
    if (!state.selectedId && state.devices.length) await selectDevice(state.devices[0].id);
    else if (state.selectedId) await selectDevice(state.selectedId);
  }

  async function selectDevice(id) {
    state.selectedId = id;
    state.dirty = {};
    state.netPayload = null;
    state.imagePayload = null;
    state.streamPayload = null;
    renderDevices();
    renderViewGrid();
    const d = selected();
    $("selTitle").textContent = d ? labelOf(d) : "View All";
    $("selMeta").textContent = d
      ? `${d.ipAddress || ""} · ${d.hardwareModel || ""} · fw ${d.firmwareVersion || "unknown"}`
      : "Live multi-camera board · drag tiles to reorder";
    ["btnSave", "btnRefreshSettings", "btnSnapshot", "btnRecStart"].forEach((bid) => {
      $(bid).disabled = !d;
    });
    await Promise.all([loadSnapshotPreview(), loadSources(), loadDeviceSettings()]);
  }

  function loadSnapshotPreview() {
    const img = $("snapImg");
    const hint = $("snapHint");
    if (!state.selectedId) {
      img.classList.remove("show");
      hint.style.display = "block";
      return;
    }
    // Use continuous live.mjpeg for device preview (smooth), not one-shot snapShot
    const url = liveStreamUrl(state.selectedId);
    img.onload = () => {
      img.classList.add("show");
      hint.style.display = "none";
    };
    img.onerror = () => {
      img.classList.remove("show");
      hint.style.display = "block";
      hint.textContent = "Live stream unavailable — check camera / ffmpeg.";
    };
    img.src = url;
  }

  async function loadSources() {
    const ul = $("sourceList");
    const kv = $("identityKv");
    const d = selected();
    kv.innerHTML = "";
    if (!d) {
      ul.innerHTML = "";
      return;
    }
    [
      ["Name", labelOf(d)],
      ["IP", d.ipAddress],
      ["Port", d.port],
      ["Model", d.hardwareModel],
      ["Firmware", d.firmwareVersion],
      ["Type", d.deviceType],
      ["ESEE", d.eseeId],
      ["Serial", d.deviceId],
      ["Login", d.loginName],
    ].forEach(([k, v]) => {
      kv.innerHTML += `<dt>${esc(k)}</dt><dd>${esc(v ?? "—")}</dd>`;
    });
    try {
      const sources = await req(`/api/devices/${d.id}/sources`);
      ul.innerHTML = (sources || [])
        .slice(0, 16)
        .map((s) => `<li><strong>${esc(s.displayName || s.kind)}</strong> r${s.rank}: ${esc(s.url)}</li>`)
        .join("");
    } catch (e) {
      ul.innerHTML = `<li>${esc(e.message)}</li>`;
    }
  }

  /** Direct NetSDK GET via service settings/write */
  async function netsdkGet(endpoint) {
    const res = await req(`/api/devices/${state.selectedId}/settings/write`, {
      method: "POST",
      body: JSON.stringify({
        endpoint,
        method: "GET",
        requireWriteVerification: false,
        snapshotBeforeWrite: false,
      }),
    });
    // Response shapes vary: { success, response } or raw
    if (res?.response != null) return res.response;
    if (res?.Response != null) return res.Response;
    if (res?.body != null) return res.body;
    if (typeof res === "string") {
      try {
        return JSON.parse(res);
      } catch {
        return res;
      }
    }
    return res;
  }

  async function netsdkPut(endpoint, payload) {
    return req(`/api/devices/${state.selectedId}/settings/write`, {
      method: "POST",
      body: JSON.stringify({
        endpoint,
        method: "PUT",
        payload,
        requireWriteVerification: false,
        snapshotBeforeWrite: true,
      }),
    });
  }

  function renderEditor(containerId, fields, prefix) {
    const root = $(containerId);
    root.innerHTML = "";
    if (!fields.length) {
      root.innerHTML = `<p class="muted">No fields available. Check camera connectivity and Reload Settings.</p>`;
      return;
    }
    fields.forEach((f) => {
      const key = f.key;
      const fullKey = `${prefix}.${key}`;
      const val = state.dirty[fullKey] !== undefined ? state.dirty[fullKey] : f.value;
      const item = document.createElement("div");
      item.className = "form-item";
      if (f.type === "bool") {
        item.innerHTML = `<label><input type="checkbox" ${val ? "checked" : ""}/> ${esc(f.label)}</label>`;
        item.querySelector("input").onchange = (e) => {
          state.dirty[fullKey] = e.target.checked;
        };
      } else if (f.type === "number") {
        const n = Number(val ?? 0);
        const min = f.min ?? 0;
        const max = f.max ?? 100;
        item.innerHTML = `<label>${esc(f.label)} <span class="val">${esc(n)}</span></label>
          <input type="range" min="${min}" max="${max}" value="${esc(n)}" />`;
        const range = item.querySelector("input");
        const span = item.querySelector(".val");
        range.oninput = () => {
          span.textContent = range.value;
          state.dirty[fullKey] = Number(range.value);
        };
      } else {
        item.innerHTML = `<label>${esc(f.label)}</label><input type="text" value="${esc(val ?? "")}" />`;
        item.querySelector("input").oninput = (e) => {
          state.dirty[fullKey] = e.target.value;
        };
      }
      root.appendChild(item);
    });
  }

  async function loadDeviceSettings() {
    if (!state.selectedId) return;
    $("imageStatus").textContent = "Loading image settings…";
    $("streamStatus").textContent = "Loading stream settings…";
    $("networkStatus").textContent = "Loading network settings…";

    // IMAGE
    try {
      const img = await netsdkGet("/NetSDK/Video/input/channel/1");
      state.imagePayload = typeof img === "object" ? img : null;
      if (state.imagePayload) {
        const p = state.imagePayload;
        renderEditor(
          "imageFields",
          [
            { key: "brightnessLevel", label: "Brightness", type: "number", value: p.brightnessLevel ?? 50 },
            { key: "contrastLevel", label: "Contrast", type: "number", value: p.contrastLevel ?? 50 },
            { key: "saturationLevel", label: "Saturation", type: "number", value: p.saturationLevel ?? 50 },
            { key: "sharpnessLevel", label: "Sharpness", type: "number", value: p.sharpnessLevel ?? 50 },
            { key: "hueLevel", label: "Hue", type: "number", value: p.hueLevel ?? 50 },
            { key: "flipEnabled", label: "Flip", type: "bool", value: !!p.flipEnabled },
            { key: "mirrorEnabled", label: "Mirror", type: "bool", value: !!p.mirrorEnabled },
            { key: "powerLineFrequencyMode", label: "Power line Hz", type: "number", value: p.powerLineFrequencyMode ?? 60, min: 50, max: 60 },
          ],
          "image"
        );
        $("imageStatus").textContent = "Loaded from /NetSDK/Video/input/channel/1";
      } else {
        renderEditor("imageFields", [], "image");
        $("imageStatus").textContent = "Unexpected image payload shape.";
      }
    } catch (e) {
      renderEditor("imageFields", [], "image");
      $("imageStatus").textContent = "Image load failed: " + e.message;
    }

    // STREAM encode 101
    try {
      const st = await netsdkGet("/NetSDK/Video/encode/channel/101");
      state.streamPayload = typeof st === "object" ? st : null;
      if (state.streamPayload) {
        const p = state.streamPayload;
        renderEditor(
          "streamFields",
          [
            { key: "codecType", label: "Codec", type: "text", value: p.codecType },
            { key: "resolution", label: "Resolution", type: "text", value: p.resolution },
            { key: "frameRate", label: "Frame rate", type: "number", value: p.frameRate ?? 15, min: 1, max: 30 },
            { key: "constantBitRate", label: "Bitrate (kbps)", type: "number", value: p.constantBitRate ?? 1536, min: 64, max: 8192 },
            { key: "bitRateControlType", label: "Rate control", type: "text", value: p.bitRateControlType },
            { key: "h264Profile", label: "Profile", type: "text", value: p.h264Profile },
            { key: "keyFrameInterval", label: "Keyframe interval", type: "number", value: p.keyFrameInterval ?? 30, min: 1, max: 200 },
            { key: "channelName", label: "Channel name", type: "text", value: p.channelName },
            { key: "enabled", label: "Enabled", type: "bool", value: p.enabled !== false },
          ],
          "stream"
        );
        $("streamStatus").textContent = "Loaded from /NetSDK/Video/encode/channel/101 (main high-res)";
      } else {
        renderEditor("streamFields", [], "stream");
        $("streamStatus").textContent = "Unexpected stream payload.";
      }
    } catch (e) {
      renderEditor("streamFields", [], "stream");
      $("streamStatus").textContent = "Stream load failed: " + e.message;
    }

    // NETWORK interface 1
    try {
      let net = await netsdkGet("/NetSDK/Network/interface/1");
      // Some firmwares return array from list endpoint
      if (Array.isArray(net)) net = net[0] || net;
      state.netPayload = typeof net === "object" ? net : null;
      if (state.netPayload) {
        const lan = state.netPayload.lan || state.netPayload.Lan || {};
        renderEditor(
          "networkFields",
          [
            { key: "interfaceName", label: "Interface", type: "text", value: state.netPayload.interfaceName || "eth0" },
            { key: "addressingType", label: "Addressing", type: "text", value: lan.addressingType ?? (lan.dhcp ? "dynamic" : "static") },
            { key: "staticIP", label: "IP address", type: "text", value: lan.staticIP },
            { key: "staticNetmask", label: "Netmask", type: "text", value: lan.staticNetmask },
            { key: "staticGateway", label: "Gateway", type: "text", value: lan.staticGateway },
            { key: "dhcp", label: "DHCP enabled", type: "bool", value: !!lan.dhcp },
            { key: "mtu", label: "MTU", type: "number", value: lan.mtu ?? 1500, min: 576, max: 9000 },
            { key: "upnp", label: "UPnP", type: "bool", value: !!(state.netPayload.upnp && state.netPayload.upnp.enabled) },
          ],
          "network"
        );
        $("networkStatus").textContent = "Loaded from /NetSDK/Network/interface/1";
      } else {
        // Fallback list
        try {
          const list = await netsdkGet("/NetSDK/Network/interface");
          $("rawSettings").textContent = JSON.stringify(list, null, 2);
          const first = Array.isArray(list) ? list[0] : list;
          state.netPayload = first;
          if (first?.lan) {
            const lan = first.lan;
            renderEditor(
              "networkFields",
              [
                { key: "staticIP", label: "IP address", type: "text", value: lan.staticIP },
                { key: "staticNetmask", label: "Netmask", type: "text", value: lan.staticNetmask },
                { key: "staticGateway", label: "Gateway", type: "text", value: lan.staticGateway },
                { key: "addressingType", label: "Addressing", type: "text", value: lan.addressingType },
                { key: "dhcp", label: "DHCP", type: "bool", value: !!lan.dhcp },
              ],
              "network"
            );
            $("networkStatus").textContent = "Loaded from /NetSDK/Network/interface list";
          } else {
            renderEditor("networkFields", [], "network");
            $("networkStatus").textContent = "No network interface payload.";
          }
        } catch (e2) {
          renderEditor("networkFields", [], "network");
          $("networkStatus").textContent = "Network load failed: " + e2.message;
        }
      }
    } catch (e) {
      renderEditor("networkFields", [], "network");
      $("networkStatus").textContent = "Network load failed: " + e.message;
    }

    $("rawSettings").textContent = JSON.stringify(
      {
        image: state.imagePayload,
        stream: state.streamPayload,
        network: state.netPayload,
        dirty: state.dirty,
      },
      null,
      2
    );
  }

  async function saveChanges() {
    if (!state.selectedId) return;
    const keys = Object.keys(state.dirty);
    if (!keys.length) {
      toast("No edits to save");
      return;
    }
    try {
      // Image
      const imgKeys = keys.filter((k) => k.startsWith("image."));
      if (imgKeys.length && state.imagePayload) {
        const p = { ...state.imagePayload };
        imgKeys.forEach((k) => {
          const field = k.slice("image.".length);
          p[field] = state.dirty[k];
        });
        await netsdkPut("/NetSDK/Video/input/channel/1", p);
      }
      // Stream
      const stKeys = keys.filter((k) => k.startsWith("stream."));
      if (stKeys.length && state.streamPayload) {
        const p = { ...state.streamPayload };
        stKeys.forEach((k) => {
          const field = k.slice("stream.".length);
          p[field] = state.dirty[k];
        });
        await netsdkPut("/NetSDK/Video/encode/channel/101", p);
      }
      // Network
      const netKeys = keys.filter((k) => k.startsWith("network."));
      if (netKeys.length && state.netPayload) {
        const p = JSON.parse(JSON.stringify(state.netPayload));
        p.lan = p.lan || {};
        netKeys.forEach((k) => {
          const field = k.slice("network.".length);
          if (field === "interfaceName") p.interfaceName = state.dirty[k];
          else if (field === "upnp") p.upnp = { ...(p.upnp || {}), enabled: !!state.dirty[k] };
          else p.lan[field] = state.dirty[k];
        });
        if (p.lan.dhcp === true) p.lan.addressingType = "dynamic";
        if (p.lan.dhcp === false && !p.lan.addressingType) p.lan.addressingType = "static";
        await netsdkPut("/NetSDK/Network/interface/1", p);
      }
      state.dirty = {};
      toast("Settings saved to camera");
      await loadDeviceSettings();
    } catch (e) {
      toast("Save failed: " + e.message, false);
    }
  }

  async function loadStoragePaths() {
    try {
      const p = await req("/api/storage/paths");
      state.storage = {
        continuousRecordings: p.continuousRecordings || p.ContinuousRecordings || "",
        highlights: p.highlights || p.Highlights || "",
        snapshots: p.snapshots || p.Snapshots || "",
      };
      $("pathContinuous").value = state.storage.continuousRecordings;
      $("pathHighlights").value = state.storage.highlights;
      $("pathSnapshots").value = state.storage.snapshots;
      $("pathStatus").textContent = "Paths loaded from server.";
    } catch (e) {
      $("pathStatus").textContent = "Could not load paths: " + e.message;
    }
  }

  async function saveStoragePaths() {
    const body = {
      continuousRecordings: $("pathContinuous").value.trim(),
      highlights: $("pathHighlights").value.trim(),
      snapshots: $("pathSnapshots").value.trim(),
    };
    if (!body.continuousRecordings || !body.highlights || !body.snapshots) {
      toast("Fill all three folder paths", false);
      return;
    }
    try {
      const p = await req("/api/storage/paths", { method: "POST", body: JSON.stringify(body) });
      state.storage = {
        continuousRecordings: p.continuousRecordings || body.continuousRecordings,
        highlights: p.highlights || body.highlights,
        snapshots: p.snapshots || body.snapshots,
      };
      $("pathContinuous").value = state.storage.continuousRecordings;
      $("pathHighlights").value = state.storage.highlights;
      $("pathSnapshots").value = state.storage.snapshots;
      toast("Save folders stored on server");
      $("pathStatus").textContent = "Saved.";
    } catch (e) {
      toast(e.message, false);
    }
  }

  function promptAllPaths() {
    const c = prompt("Continuous recordings folder (server path):", $("pathContinuous").value);
    if (c === null) return;
    const h = prompt("Highlights folder (server path):", $("pathHighlights").value);
    if (h === null) return;
    const s = prompt("Snapshots folder (server path):", $("pathSnapshots").value);
    if (s === null) return;
    $("pathContinuous").value = c.trim();
    $("pathHighlights").value = h.trim();
    $("pathSnapshots").value = s.trim();
    saveStoragePaths();
  }

  async function ensureContinuousPath() {
    let path = $("pathContinuous").value.trim() || state.storage.continuousRecordings;
    if (!path) {
      path = prompt("Where should continuous recordings be saved? (server folder path)") || "";
      if (!path.trim()) throw new Error("Recording folder required");
      $("pathContinuous").value = path.trim();
      await saveStoragePaths();
      path = $("pathContinuous").value.trim();
    }
    return path;
  }

  async function ensureHighlightPath() {
    let path = $("pathHighlights").value.trim() || state.storage.highlights;
    if (!path) {
      path = prompt("Where should highlight clips be saved? (server folder path)") || "";
      if (!path.trim()) throw new Error("Highlights folder required");
      $("pathHighlights").value = path.trim();
      await saveStoragePaths();
      path = $("pathHighlights").value.trim();
    }
    return path;
  }

  async function startRecording(deviceId) {
    const id = deviceId || state.selectedId;
    if (!id) return toast("Select a camera", false);
    try {
      const outputDirectory = await ensureContinuousPath();
      const job = await req("/api/recordings/start", {
        method: "POST",
        body: JSON.stringify({ deviceId: id, outputDirectory }),
      });
      toast(`Recording → ${job.outputDirectory || outputDirectory}`);
      await refreshRec();
    } catch (e) {
      toast(e.message, false);
    }
  }

  async function saveSnapshot(deviceId) {
    const id = deviceId || state.selectedId;
    if (!id) return toast("Select a camera", false);
    try {
      // Ensure snapshot folder is set
      if (!$("pathSnapshots").value.trim()) {
        const s = prompt("Where should snapshots be saved? (server folder path)", state.storage.snapshots || "");
        if (!s) return;
        $("pathSnapshots").value = s.trim();
        await saveStoragePaths();
      }
      const result = await req(`/api/storage/save-snapshot/${id}`, { method: "POST", body: "{}" });
      toast(`Snapshot saved: ${result.path || "ok"}`);
    } catch (e) {
      // Fallback: open snapshot in new tab for manual save
      window.open(snapUrl(id), "_blank");
      toast("Server save failed — opened snapshot in new tab: " + e.message, false);
    }
  }

  async function refreshRec() {
    try {
      $("recJobs").textContent = JSON.stringify(await req("/api/recordings/jobs"), null, 2);
    } catch (e) {
      $("recJobs").textContent = e.message;
    }
    try {
      $("recIndex").textContent = JSON.stringify(await req("/api/recordings/index?limit=40"), null, 2);
    } catch (e) {
      $("recIndex").textContent = e.message;
    }
  }

  async function loadHighlights() {
    try {
      const s = await req("/api/highlights");
      $("hlSelected").textContent = s.selected
        ? `Selected #${s.selectedIndex}: ${s.selected.displayName} (${s.selected.ipAddress}) · ${s.preferredStream}`
        : "No highlight selected";
      const tiles = $("hlTiles");
      tiles.innerHTML = "";
      (s.tiles || []).forEach((t, i) => {
        const div = document.createElement("div");
        div.className = "tile" + (i === s.selectedIndex ? " selected" : "");
        div.innerHTML = `<strong>${esc(t.displayName)}</strong>
          <div class="sub">${esc(t.ipAddress)} · ${esc(t.hardwareModel || "")}</div>`;
        div.onclick = async () => {
          await req(`/api/highlights/select/${t.deviceId}`, { method: "POST" });
          await loadHighlights();
          await selectDevice(t.deviceId);
        };
        tiles.appendChild(div);
      });
    } catch (e) {
      $("hlSelected").textContent = e.message;
    }
  }

  function bindTabs() {
    document.querySelectorAll("#tabs button").forEach((btn) => {
      btn.onclick = () => {
        document.querySelectorAll("#tabs button").forEach((b) => b.classList.remove("active"));
        document.querySelectorAll(".panel").forEach((p) => p.classList.remove("active"));
        btn.classList.add("active");
        $("tab-" + btn.dataset.tab).classList.add("active");
        if (btn.dataset.tab === "viewall") {
          renderViewGrid();
          scheduleLiveRefresh();
        }
        if (btn.dataset.tab === "record") {
          loadStoragePaths();
          refreshRec();
        }
        if (btn.dataset.tab === "highlights") loadHighlights();
        if (btn.dataset.tab === "image" || btn.dataset.tab === "stream" || btn.dataset.tab === "network") {
          loadDeviceSettings();
        }
      };
    });
  }

  function bindActions() {
    document.querySelectorAll("#layoutBtns button").forEach((b) => {
      b.onclick = () => setLayout(Number(b.dataset.layout));
    });
    if ($("liveRefresh")) $("liveRefresh").onchange = () => { attachLiveStreams(); scheduleLiveRefresh(); };
    if ($("liveInterval")) $("liveInterval").onchange = scheduleLiveRefresh;
    if ($("streamQuality")) $("streamQuality").onchange = () => { attachLiveStreams(); loadSnapshotPreview(); };
    $("btnResetOrder").onclick = () => {
      localStorage.removeItem(LS_ORDER);
      syncOrder();
      renderViewGrid();
      toast("View order reset");
    };

    $("btnRefresh").onclick = () => loadDevices().then(() => toast("Refreshed"));
    $("btnDiscover").onclick = async () => {
      try {
        await req("/api/devices/discover", { method: "POST" });
        await loadDevices();
        toast("Discovery complete");
      } catch (e) {
        toast(e.message, false);
      }
    };
    $("btnRegisterAegon").onclick = async () => {
      try {
        const lorexPassword = prompt("Lorex password (blank if unknown)") ?? "";
        const wvcPassword = prompt("WVC password (blank if unknown)") ?? "";
        await req("/api/devices/register-aegon-lan", {
          method: "POST",
          body: JSON.stringify({ lorexPassword, wvcPassword }),
        });
        await loadDevices();
        toast("LAN cameras registered");
      } catch (e) {
        toast(e.message, false);
      }
    };
    $("btnAddCam").onclick = async () => {
      try {
        const ip = $("regIp").value.trim();
        if (!ip) return toast("Enter IP", false);
        await req("/api/devices/register", {
          method: "POST",
          body: JSON.stringify({
            ipAddress: ip,
            port: 80,
            loginName: "admin",
            password: $("regPass").value,
            hardwareModel: "",
          }),
        });
        await loadDevices();
        toast("Camera added: " + ip);
      } catch (e) {
        toast(e.message, false);
      }
    };
    $("btnSave").onclick = () => saveChanges();
    $("btnRefreshSettings").onclick = () => loadDeviceSettings().then(() => toast("Settings reloaded"));
    $("btnSnapshot").onclick = () => saveSnapshot();

    $("btnSavePaths").onclick = () => saveStoragePaths();
    $("btnPromptPaths").onclick = () => promptAllPaths();
    $("btnPathContDefault").onclick = async () => {
      await loadStoragePaths();
      toast("Defaults reloaded from server");
    };
    $("btnPathHlDefault").onclick = () => $("btnPathContDefault").onclick();
    $("btnPathSnapDefault").onclick = () => $("btnPathContDefault").onclick();

    $("btnRecStart").onclick = () => startRecording();
    $("btnRecStartAll").onclick = async () => {
      try {
        const outputDirectory = await ensureContinuousPath();
        // Start each device into continuous folder
        for (const d of state.devices) {
          try {
            await req("/api/recordings/start", {
              method: "POST",
              body: JSON.stringify({ deviceId: d.id, outputDirectory: `${outputDirectory}/${(d.ipAddress || d.id).replace(/\./g, "_")}` }),
            });
          } catch (e) {
            console.warn("start failed", d.ipAddress, e);
          }
        }
        toast("Started recordings for registered cameras");
        await refreshRec();
      } catch (e) {
        toast(e.message, false);
      }
    };
    $("btnRecStopAll").onclick = async () => {
      try {
        await req("/api/recordings/stop-all", { method: "POST" });
        toast("Stopped recordings");
        await refreshRec();
      } catch (e) {
        toast(e.message, false);
      }
    };
    $("btnRecIndex").onclick = async () => {
      try {
        await req("/api/recordings/index/refresh", { method: "POST" });
        await refreshRec();
        toast("Index refreshed");
      } catch (e) {
        toast(e.message, false);
      }
    };

    $("btnHlNext").onclick = async () => {
      await req("/api/highlights/next", { method: "POST" });
      await loadHighlights();
    };
    $("btnHlPrev").onclick = async () => {
      await req("/api/highlights/prev", { method: "POST" });
      await loadHighlights();
    };
    $("btnHlMain").onclick = async () => {
      await req("/api/highlights/stream/main", { method: "POST" });
      await loadHighlights();
    };
    $("btnHlSub").onclick = async () => {
      await req("/api/highlights/stream/sub", { method: "POST" });
      await loadHighlights();
    };
    $("btnHlRec").onclick = async () => {
      try {
        const hl = await req("/api/highlights");
        const id = hl.selectedDeviceId || hl.selected?.deviceId;
        if (!id) return toast("No highlight selected", false);
        const outputDirectory = await ensureHighlightPath();
        await req("/api/recordings/start", {
          method: "POST",
          body: JSON.stringify({ deviceId: id, outputDirectory }),
        });
        toast("Highlight recording started → " + outputDirectory);
      } catch (e) {
        toast(e.message, false);
      }
    };
  }

  async function boot() {
    if (![1, 2, 4, 5, 6, 7, 8].includes(state.layout)) state.layout = 4;
    bindTabs();
    bindActions();
    setLayout(state.layout);
    $("platformInfo").textContent = `Operator UI: multi-view live board\nAPI: ${location.origin}\nUA: ${navigator.userAgent}`;
    try {
      const h = await req("/api/health");
      $("healthLine").textContent = `API ok · ${h.platform || ""} · ${h.timestamp || ""}`;
    } catch {
      $("healthLine").textContent = "API unreachable";
    }
    await loadStoragePaths();
    await loadDevices();
    await loadHighlights();
    await refreshRec();
    scheduleLiveRefresh();
  }

  boot();
})();
