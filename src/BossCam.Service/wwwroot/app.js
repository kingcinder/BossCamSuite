(() => {
  const api = "";
  const state = {
    devices: [],
    selectedId: null,
    typed: [],
    dirty: {},
  };

  const $ = (id) => document.getElementById(id);
  const toast = (msg, ok = true) => {
    const el = $("toast");
    el.textContent = msg;
    el.className = `toast ${ok ? "ok" : "bad"}`;
    clearTimeout(toast._t);
    toast._t = setTimeout(() => el.classList.add("hidden"), 4200);
  };

  async function req(path, opts = {}) {
    const res = await fetch(api + path, {
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

  function renderDevices() {
    const list = $("deviceList");
    list.innerHTML = "";
    state.devices.forEach((d) => {
      const li = document.createElement("li");
      if (d.id === state.selectedId) li.classList.add("active");
      li.innerHTML = `<div class="name">${esc(d.displayName || d.name || d.ipAddress)}</div>
        <div class="sub">${esc(d.ipAddress || "—")} · ${esc(d.hardwareModel || d.deviceType || "camera")}</div>`;
      li.onclick = () => selectDevice(d.id);
      list.appendChild(li);
    });
  }

  function esc(s) {
    return String(s ?? "")
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;")
      .replaceAll('"', "&quot;");
  }

  async function loadDevices() {
    state.devices = (await req("/api/devices")) || [];
    renderDevices();
    if (state.selectedId && !selected()) state.selectedId = null;
    if (!state.selectedId && state.devices.length) await selectDevice(state.devices[0].id);
  }

  async function selectDevice(id) {
    state.selectedId = id;
    state.dirty = {};
    renderDevices();
    const d = selected();
    $("selTitle").textContent = d ? d.displayName || d.name || d.ipAddress : "No camera selected";
    $("selMeta").textContent = d
      ? `${d.ipAddress || ""} · ${d.hardwareModel || ""} · fw ${d.firmwareVersion || "unknown"}`
      : "Select a device to control image, network, stream, and recording.";
    ["btnSave", "btnRefreshSettings", "btnSnapshot", "btnRecStart"].forEach((id) => {
      $(id).disabled = !d;
    });
    await Promise.all([loadSnapshot(), loadSources(), loadTyped()]);
  }

  async function loadSnapshot() {
    const img = $("snapImg");
    const hint = $("snapHint");
    if (!state.selectedId) {
      img.classList.remove("show");
      hint.style.display = "block";
      return;
    }
    try {
      const url = `/api/devices/${state.selectedId}/snapshot?t=${Date.now()}`;
      img.onload = () => {
        img.classList.add("show");
        hint.style.display = "none";
      };
      img.onerror = () => {
        img.classList.remove("show");
        hint.style.display = "block";
        hint.textContent = "Snapshot unavailable (auth or brand path).";
      };
      img.src = url;
    } catch {
      hint.textContent = "Snapshot failed.";
    }
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
    const rows = [
      ["Name", d.displayName || d.name],
      ["IP", d.ipAddress],
      ["Port", d.port],
      ["Model", d.hardwareModel],
      ["Firmware", d.firmwareVersion],
      ["Type", d.deviceType],
      ["ESEE", d.eseeId],
      ["Serial", d.deviceId],
      ["Login", d.loginName],
    ];
    rows.forEach(([k, v]) => {
      kv.innerHTML += `<dt>${esc(k)}</dt><dd>${esc(v ?? "—")}</dd>`;
    });
    try {
      const sources = await req(`/api/devices/${d.id}/sources`);
      ul.innerHTML = (sources || [])
        .slice(0, 12)
        .map((s) => `<li><strong>${esc(s.displayName || s.kind)}</strong> r${s.rank}: ${esc(s.url)}</li>`)
        .join("");
    } catch (e) {
      ul.innerHTML = `<li>${esc(e.message)}</li>`;
    }
  }

  function collectFields(groups, keys) {
    const map = new Map();
    (groups || []).forEach((g) => {
      (g.fields || g.Fields || []).forEach((f) => {
        const key = f.fieldKey || f.FieldKey || f.key;
        if (key) map.set(key, f);
      });
    });
    // Also flatten normalized-like objects if shape differs
    if (map.size === 0 && Array.isArray(groups)) {
      groups.forEach((g) => {
        Object.entries(g.values || g.Values || {}).forEach(([k, v]) => {
          map.set(k, { fieldKey: k, value: v?.value ?? v, displayName: k });
        });
      });
    }
    return keys.map((k) => map.get(k)).filter(Boolean);
  }

  function fieldValue(f) {
    const v = f.value ?? f.Value ?? f.editableValue ?? f.EditableValue;
    if (v && typeof v === "object" && "value" in v) return v.value;
    return v;
  }

  function renderFieldEditors(containerId, fields) {
    const root = $(containerId);
    root.innerHTML = "";
    if (!fields.length) {
      root.innerHTML = `<p class="muted">No typed fields loaded for this group. Use Advanced → raw snapshot or Reload Settings.</p>`;
      return;
    }
    fields.forEach((f) => {
      const key = f.fieldKey || f.FieldKey || f.key;
      const label = f.displayName || f.DisplayName || key;
      const val = state.dirty[key] !== undefined ? state.dirty[key] : fieldValue(f);
      const item = document.createElement("div");
      item.className = "form-item";
      const isNum = typeof val === "number" || /level|bitrate|rate|brightness|contrast|saturation|sharpness|hue|gamma/i.test(key);
      const isBool = typeof val === "boolean" || /enabled|flip|mirror|dhcp/i.test(key);
      if (isBool) {
        item.innerHTML = `<label><input type="checkbox" data-key="${esc(key)}" ${val ? "checked" : ""}/> ${esc(label)}</label>`;
        item.querySelector("input").onchange = (e) => {
          state.dirty[key] = e.target.checked;
        };
      } else if (isNum) {
        const n = Number(val ?? 0);
        item.innerHTML = `<label>${esc(label)} <span class="val" id="v-${esc(key)}">${esc(n)}</span></label>
          <input type="range" min="0" max="100" value="${esc(n)}" data-key="${esc(key)}" />`;
        const range = item.querySelector("input");
        range.oninput = () => {
          $("v-" + key).textContent = range.value;
          state.dirty[key] = Number(range.value);
        };
      } else {
        item.innerHTML = `<label>${esc(label)}</label><input type="text" data-key="${esc(key)}" value="${esc(val ?? "")}" />`;
        item.querySelector("input").oninput = (e) => {
          state.dirty[key] = e.target.value;
        };
      }
      root.appendChild(item);
    });
  }

  async function loadTyped() {
    if (!state.selectedId) return;
    try {
      // Prefer refresh from device
      state.typed = (await req(`/api/devices/${state.selectedId}/settings/typed/refresh`, { method: "POST", body: "null" })) || [];
    } catch {
      try {
        state.typed = (await req(`/api/devices/${state.selectedId}/settings/typed`)) || [];
      } catch (e) {
        state.typed = [];
        $("rawSettings").textContent = e.message;
      }
    }

    const imageKeys = ["brightness", "contrast", "saturation", "sharpness", "hue", "gamma", "flipEnabled", "mirrorEnabled", "wdr", "denoise"];
    const streamKeys = ["codec", "profile", "resolution", "bitrate", "frameRate", "bitRateControlType", "codecType", "constantBitRate"];
    const networkKeys = ["addressingType", "staticIP", "staticNetmask", "staticGateway", "wirelessMode", "wirelessApEssId", "dhcpMode", "eseeEnabled"];

    // Flatten: typed may be group snapshots with nested fields
    let flat = [];
    (state.typed || []).forEach((g) => {
      if (Array.isArray(g.fields)) flat = flat.concat(g.fields.map((f) => ({ ...f, group: g.groupName || g.name })));
      else if (Array.isArray(g.Fields)) flat = flat.concat(g.Fields.map((f) => ({ ...f, group: g.GroupName || g.Name })));
    });

    const byKey = (keys) =>
      keys
        .map((k) => flat.find((f) => (f.fieldKey || f.FieldKey || "").toLowerCase() === k.toLowerCase() || (f.fieldKey || "").includes(k)))
        .filter(Boolean);

    // If still empty, synthesize from raw settings groups
    if (!flat.length) {
      try {
        const snap = await req(`/api/devices/${state.selectedId}/settings`);
        $("rawSettings").textContent = JSON.stringify(snap, null, 2);
        // Build synthetic image fields from video input payload if present
        const groups = snap?.groups || [];
        const video = groups.find((g) => /video/i.test(g.name || ""));
        const vals = video?.values || {};
        for (const [k, entry] of Object.entries(vals)) {
          const node = entry?.value ?? entry;
          if (node && typeof node === "object" && !Array.isArray(node)) {
            for (const [fk, fv] of Object.entries(node)) {
              if (["brightnessLevel", "contrastLevel", "saturationLevel", "sharpnessLevel", "hueLevel", "flipEnabled", "mirrorEnabled"].includes(fk)) {
                flat.push({ fieldKey: fk.replace("Level", "").replace("Enabled", "Enabled"), value: fv, displayName: fk });
              }
            }
          }
        }
      } catch (e) {
        $("rawSettings").textContent = e.message;
      }
    } else {
      $("rawSettings").textContent = JSON.stringify(state.typed, null, 2);
    }

    renderFieldEditors("imageFields", byKey(imageKeys).length ? byKey(imageKeys) : flat.filter((f) => /bright|contrast|sat|sharp|hue|flip|mirror|gamma|wdr|denoise/i.test(f.fieldKey || f.displayName || "")));
    renderFieldEditors("streamFields", byKey(streamKeys).length ? byKey(streamKeys) : flat.filter((f) => /codec|resol|bit|frame|profile/i.test(f.fieldKey || f.displayName || "")));
    renderFieldEditors("networkFields", byKey(networkKeys).length ? byKey(networkKeys) : flat.filter((f) => /ip|gateway|mask|wireless|dhcp|esee|dns/i.test(f.fieldKey || f.displayName || "")));
  }

  async function saveChanges() {
    if (!state.selectedId) return;
    const changes = Object.entries(state.dirty).map(([fieldKey, value]) => ({ fieldKey, value }));
    if (!changes.length) {
      toast("No edits to save");
      return;
    }
    try {
      const result = await req(`/api/devices/${state.selectedId}/settings/typed/apply-batch`, {
        method: "POST",
        body: JSON.stringify({ changes, expertOverride: true }),
      });
      state.dirty = {};
      toast(`Saved ${Array.isArray(result) ? result.filter((r) => r.success).length : "?"} change(s)`);
      await loadTyped();
    } catch (e) {
      // Fallback: direct write for brightness etc via video input channel
      try {
        await applyDirectNetSdkFallback(changes);
        toast("Saved via NetSDK direct write");
        state.dirty = {};
        await loadTyped();
      } catch (e2) {
        toast(e2.message || e.message, false);
      }
    }
  }

  async function applyDirectNetSdkFallback(changes) {
    // Read full main image object, merge known fields, PUT back (durable on Juan).
    const d = selected();
    if (!d?.ipAddress) throw new Error("No device IP");
    // Use service write API with full payload if possible
    const map = Object.fromEntries(changes.map((c) => [c.fieldKey, c.value]));
    const endpoint = "/NetSDK/Video/input/channel/1";
    // GET via write plan GET through settings API is awkward; use typed apply expert already failed.
    // Send PUT with known fields only through settings/write when we have payload from raw.
    const snap = await req(`/api/devices/${d.id}/settings`);
    const videoGroup = (snap.groups || []).find((g) => /video/i.test(g.name || ""));
    let payload = null;
    for (const [k, entry] of Object.entries(videoGroup?.values || {})) {
      if (String(k).includes("/Video/input/channel/1") || String(k).endsWith("channel/1")) {
        payload = entry?.value ?? entry;
        break;
      }
    }
    if (!payload || typeof payload !== "object") throw new Error("Could not load channel/1 payload for direct write");
    const p = { ...payload };
    if (map.brightness != null) p.brightnessLevel = Number(map.brightness);
    if (map.brightnessLevel != null) p.brightnessLevel = Number(map.brightnessLevel);
    if (map.contrast != null) p.contrastLevel = Number(map.contrast);
    if (map.contrastLevel != null) p.contrastLevel = Number(map.contrastLevel);
    if (map.saturation != null) p.saturationLevel = Number(map.saturation);
    if (map.saturationLevel != null) p.saturationLevel = Number(map.saturationLevel);
    if (map.sharpness != null) p.sharpnessLevel = Number(map.sharpness);
    if (map.sharpnessLevel != null) p.sharpnessLevel = Number(map.sharpnessLevel);
    if (map.hue != null) p.hueLevel = Number(map.hue);
    if (map.flipEnabled != null) p.flipEnabled = !!map.flipEnabled;
    if (map.mirrorEnabled != null) p.mirrorEnabled = !!map.mirrorEnabled;
    await req(`/api/devices/${d.id}/settings/write`, {
      method: "POST",
      body: JSON.stringify({
        endpoint,
        method: "PUT",
        payload: p,
        requireWriteVerification: false,
        snapshotBeforeWrite: true,
      }),
    });
  }

  async function refreshRec() {
    try {
      $("recJobs").textContent = JSON.stringify(await req("/api/recordings/jobs"), null, 2);
    } catch (e) {
      $("recJobs").textContent = e.message;
    }
    try {
      $("recIndex").textContent = JSON.stringify(await req("/api/recordings/index?limit=30"), null, 2);
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
          <div class="sub">${esc(t.ipAddress)} · ${esc(t.hardwareModel || "")}</div>
          <div class="sub">${esc((t.recordUrl || t.liveUrl || "").slice(0, 80))}</div>`;
        div.onclick = async () => {
          await req(`/api/highlights/select/${t.deviceId}`, { method: "POST" });
          await loadHighlights();
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
        if (btn.dataset.tab === "record") refreshRec();
        if (btn.dataset.tab === "highlights") loadHighlights();
      };
    });
  }

  function bindActions() {
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
    $("btnRefreshSettings").onclick = () => loadTyped().then(() => toast("Settings reloaded"));
    $("btnSnapshot").onclick = () => loadSnapshot();
    $("btnRecStart").onclick = async () => {
      try {
        await req("/api/recordings/start", { method: "POST", body: JSON.stringify({ deviceId: state.selectedId }) });
        toast("Recording started (high-res main)");
        await refreshRec();
      } catch (e) {
        toast(e.message, false);
      }
    };
    $("btnRecStartAll").onclick = async () => {
      try {
        await req("/api/recordings/start-all", { method: "POST" });
        toast("Started all IPC recordings");
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
        await req("/api/highlights/record-selected", { method: "POST" });
        toast("Highlight recording started");
      } catch (e) {
        toast(e.message, false);
      }
    };
  }

  async function boot() {
    bindTabs();
    bindActions();
    $("platformInfo").textContent = `Operator UI: Linux-native web console\nAPI: ${location.origin}\nUser agent: ${navigator.userAgent}`;
    try {
      const h = await req("/api/health");
      $("healthLine").textContent = `API ok · ${h.timestamp || ""}`;
    } catch {
      $("healthLine").textContent = "API unreachable";
    }
    await loadDevices();
    await loadHighlights();
    await refreshRec();
  }

  boot();
})();
