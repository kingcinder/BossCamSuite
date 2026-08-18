import type { HealthResponse, DeviceIdentity, VideoSourceDescriptor, LiveMediaManifest, MediaStoragePaths, RecordingJob, RecordingSegment, HighlightState, WritePlan, FieldDef, FirmwareArtifact, UserAccount, PersistenceVerificationResult, ControlPointInventoryReport, WriteResult, TypedSettingGroupSnapshot, ClipExportRequest, ClipExportResult, EnrollDeviceRequest, EnrollDeviceResult, OnvifCredentialScanResult, CgiFuzzResult, CameraApInfo, CameraRecoveryRunStatus, AutoRecoveryStatus } from './types';

const LS_LAN_TOKEN = 'bosscam.lanToken';

/* ── HTTP helpers ────────────────────────────────────────────── */

async function request<T>(path: string, opts: RequestInit = {}): Promise<T> {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...(opts.headers as Record<string, string> || {}),
  };
  const token = localStorage.getItem(LS_LAN_TOKEN);
  if (token && !path.startsWith('/api/auth/whoami')) {
    headers['X-LAN-Token'] = token;
  }

  const doFetch = (customHeaders: Record<string, string> = {}): Promise<Response> =>
    fetch(path, { ...opts, headers: { ...headers, ...customHeaders } });

  let res = await doFetch();

  // 401 → prompt for LAN token, retry once
  if (res.status === 401 && path.startsWith('/api')) {
    const entered = window.prompt(
      `LAN token required for ${path}. Enter the token configured on the BossCamSuite host (Cancel to abort).`
    );
    if (entered?.trim()) {
      const candidate = entered.trim();
      const retryHeaders = { ...headers, 'X-LAN-Token': candidate };
      const retryRes = await fetch(path, { ...opts, headers: retryHeaders });
      if (retryRes.ok) {
        localStorage.setItem(LS_LAN_TOKEN, candidate);
        res = retryRes;
      } else {
        localStorage.removeItem(LS_LAN_TOKEN);
        res = retryRes;
      }
    } else {
      localStorage.removeItem(LS_LAN_TOKEN);
    }
  }

  if (!res.ok) {
    const text = await res.text().catch(() => `${res.status} ${res.statusText}`);
    throw new Error(text || `${res.status} ${res.statusText}`);
  }

  if (res.status === 204) return null as T;
  const ct = res.headers.get('content-type') || '';
  if (ct.includes('application/json')) return res.json();
  return res.text() as unknown as T;
}

/* ── API functions ───────────────────────────────────────────── */

export const api = {
  /** GET /api/health */
  health: () => request<HealthResponse>('/api/health'),

  /** GET /api/devices */
  devices: () => request<DeviceIdentity[]>('/api/devices'),

  /** GET /api/devices/stars — ids of cameras pinned to the landing page (server-side, mirrors desktop) */
  stars: () => request<{ deviceIds: string[] }>('/api/devices/stars'),

  /** PUT /api/devices/{id}/star — pin/unpin a camera to the landing page (server-side) */
  setStar: (id: string, starred: boolean) =>
    request<{ deviceId: string; starred: boolean }>(`/api/devices/${id}/star`, {
      method: 'PUT',
      body: JSON.stringify({ starred }),
    }),

  /** POST /api/devices/discover */
  discover: () => request<DeviceIdentity[]>('/api/devices/discover', { method: 'POST' }),

  /** POST /api/devices/discover with ipRangeOverride='auto' — forces the subnet sweep (Scan subnet) */
  scanSubnet: () =>
    request<DeviceIdentity[]>('/api/devices/discover', {
      method: 'POST',
      body: JSON.stringify({ ipRangeOverride: 'auto' }),
    }),

  /** POST /api/devices/register */
  register: (body: { ipAddress: string; port?: number; loginName?: string; password?: string; hardwareModel?: string }) =>
    request<DeviceIdentity>('/api/devices/register', { method: 'POST', body: JSON.stringify(body) }),

  /** POST /api/devices/register-aegon-lan */
  registerAegonLan: (lorexPassword?: string, wvcPassword?: string) =>
    request<DeviceIdentity[]>('/api/devices/register-aegon-lan', {
      method: 'POST',
      body: JSON.stringify({ lorexPassword, wvcPassword }),
    }),

  /** POST /api/devices/enroll — one-click enroll (probe → merge → optional continuous record) */
  enroll: (body: EnrollDeviceRequest) =>
    request<EnrollDeviceResult>('/api/devices/enroll', { method: 'POST', body: JSON.stringify(body) }),

  /** POST /api/devices/enroll-batch — enroll all discovered cameras */
  enrollBatch: (requests: EnrollDeviceRequest[]) =>
    request<EnrollDeviceResult[]>('/api/devices/enroll-batch', {
      method: 'POST',
      body: JSON.stringify(requests),
    }),

  /** GET /api/devices/{id}/sources */
  sources: (id: string) => request<VideoSourceDescriptor[]>(`/api/devices/${id}/sources`),

  /** Snapshot URL (not an API call, returns an image) */
  snapshotUrl: (id: string) => `/api/devices/${id}/snapshot?t=${Date.now()}`,

  /** GET /api/devices/{id}/live-manifest — negotiated media modes and fallback URLs */
  liveManifest: (id: string, quality = 'sub') =>
    request<LiveMediaManifest>(`/api/devices/${id}/live-manifest?quality=${encodeURIComponent(quality)}`),

  /** Live MJPEG URL */
  liveMjpegUrl: (id: string, quality = 'sub') =>
    `/api/devices/${id}/live.mjpeg?quality=${encodeURIComponent(quality)}&t=${Date.now()}`,

  /** POST /api/devices/{id}/settings/write — used for both read (GET via write) and write (PUT via write) */
  settingsWrite: (id: string, plan: WritePlan) =>
    request<{ success: boolean; response?: unknown; Response?: unknown; body?: unknown; Message?: string; message?: string }>(
      `/api/devices/${id}/settings/write`,
      { method: 'POST', body: JSON.stringify(plan) }
    ),

  /** Convenience: read a device setting endpoint via the write API (GET) */
  async settingGet(id: string, endpoint: string): Promise<unknown> {
    const res = await this.settingsWrite(id, {
      endpoint,
      method: 'GET',
      requireWriteVerification: false,
      snapshotBeforeWrite: false,
    });
    return res?.response ?? res?.Response ?? res?.body ?? res;
  },

  /** Convenience: write a device setting via the write API (PUT) */
  settingPut(id: string, endpoint: string, payload: unknown) {
    return this.settingsWrite(id, {
      endpoint,
      method: 'PUT',
      payload,
      requireWriteVerification: false,
      snapshotBeforeWrite: true,
    });
  },

  /** GET /api/storage/paths */
  storagePaths: () => request<MediaStoragePaths>('/api/storage/paths'),

  /** POST /api/storage/paths */
  saveStoragePaths: (paths: MediaStoragePaths) =>
    request<MediaStoragePaths>('/api/storage/paths', { method: 'POST', body: JSON.stringify(paths) }),

  /** POST /api/storage/save-snapshot/{id} */
  saveSnapshot: (id: string) =>
    request<{ path: string; bytes: number }>(`/api/storage/save-snapshot/${id}`, {
      method: 'POST',
      body: '{}',
    }),

  /** POST /api/recordings/start */
  recordingStart: (body: { deviceId: string; outputDirectory?: string }) =>
    request<RecordingJob>('/api/recordings/start', { method: 'POST', body: JSON.stringify(body) }),

  /** POST /api/recordings/stop-all */
  /** POST /api/recordings/start-all */
  recordingStartAll: () => request<RecordingJob[]>('/api/recordings/start-all', { method: 'POST' }),

  /** POST /api/recordings/stop/{jobId} */
  recordingStop: (jobId: string) => request<RecordingJob>(`/api/recordings/stop/${jobId}`, { method: 'POST' }),

  recordingStopAll: () => request<RecordingJob[]>('/api/recordings/stop-all', { method: 'POST' }),

  /** GET /api/recordings/jobs */
  recordingJobs: () => request<RecordingJob[]>('/api/recordings/jobs'),

  /** POST /api/recordings/index/refresh */
  recordingIndexRefresh: (deviceId?: string) =>
    request<RecordingSegment[]>(`/api/recordings/index/refresh${deviceId ? `?deviceId=${deviceId}` : ''}`, {
      method: 'POST',
    }),

  /** GET /api/recordings/index */
  recordingIndex: (limit = 40) => request<RecordingSegment[]>(`/api/recordings/index?limit=${limit}`),

  /** POST /api/recordings/export — clip export with copy-first concat */
  recordingExport: (body: ClipExportRequest) =>
    request<ClipExportResult>('/api/recordings/export', { method: 'POST', body: JSON.stringify(body) }),

  /** GET /api/recordings/download — download an exported clip by server path */
  recordingDownloadUrl: (path: string) => `/api/recordings/download?path=${encodeURIComponent(path)}`,

  /** GET /api/devices/{id}/settings/typed — current typed settings with live values */
  typedSettings: (id: string) => request<TypedSettingGroupSnapshot[]>(`/api/devices/${id}/settings/typed`),

  /** GET /api/devices/{id}/control-points */
  controlPoints: (id: string) => request<ControlPointInventoryReport>(`/api/devices/${id}/control-points`),

  /** GET /api/highlights */
  highlights: () => request<HighlightState>('/api/highlights'),

  /** POST /api/highlights/select/{deviceId} */
  highlightSelect: (deviceId: string) =>
    request<HighlightState>(`/api/highlights/select/${deviceId}`, { method: 'POST' }),

  /** POST /api/highlights/next */
  highlightNext: () => request<HighlightState>('/api/highlights/next', { method: 'POST' }),

  /** POST /api/highlights/prev */
  highlightPrev: () => request<HighlightState>('/api/highlights/prev', { method: 'POST' }),

  /** POST /api/highlights/stream/{mode} */
  highlightStream: (mode: 'main' | 'sub') =>
    request<HighlightState>(`/api/highlights/stream/${mode}`, { method: 'POST' }),

  /** POST /api/highlights/record-selected */
  highlightRecord: () => request<{ message: string }>('/api/highlights/record-selected', { method: 'POST' }),

  // ── Firmware ──────────────────────────────────────────────────

  /** POST /api/firmware/register */
  firmwareRegister: (filePath: string) =>
    request<{ success: boolean; message?: string }>('/api/firmware/register', {
      method: 'POST',
      body: JSON.stringify({ filePath }),
    }),

  /** GET /api/firmware */
  firmwareList: () => request<FirmwareArtifact[]>('/api/firmware'),

  // ── User Accounts (via maintenance endpoint) ──────────────────

  /** POST /api/devices/{id}/maintenance/RefreshUsers - list users (returns MaintenanceResult with raw camera response) */
  userList: (deviceId: string) =>
    request<{ body?: unknown; response?: unknown; message?: string; Message?: string }>(
      `/api/devices/${deviceId}/maintenance/RefreshUsers`,
      { method: 'POST', body: '{}' }
    ),

  /** POST /api/devices/{id}/maintenance/PasswordReset - change password */
  userChangePassword: (deviceId: string, username: string, newPassword: string) =>
    request<{ body?: unknown; response?: unknown; message?: string; Message?: string }>(
      `/api/devices/${deviceId}/maintenance/PasswordReset`,
      {
        method: 'POST',
        body: JSON.stringify({ username, newPassword }),
      }
    ),

  /** POST /api/devices/{id}/maintenance/TimeSync - sync the camera clock to the host */
  syncCameraClock: (deviceId: string) =>
    request<{ success?: boolean; message?: string; Message?: string }>(
      `/api/devices/${deviceId}/maintenance/TimeSync`,
      { method: 'POST', body: '{}' }
    ),

  // ── Persistence Verification ──────────────────────────────────

  /** GET /api/devices/{id}/persistence */
  persistenceResults: (deviceId: string, limit = 20) =>
    request<PersistenceVerificationResult[]>(`/api/devices/${deviceId}/persistence?limit=${limit}`),

  // ── Motion Grid ────────────────────────────────────────────────

  /** Read motion detection grid from the camera */
  motionGridGet: (deviceId: string) =>
    api.settingGet(deviceId, '/NetSDK/Video/motionDetection/channel/1'),

  /** Write motion detection grid to the camera */
  motionGridPut: (deviceId: string, payload: unknown) =>
    api.settingPut(deviceId, '/NetSDK/Video/motionDetection/channel/1', payload),

  /** GET /api/devices/connectivity — all device connectivity snapshots */
  connectivityAll: () => request<Array<{ deviceId: string; status: string; transportResults?: Record<string, boolean>; lastCheckedAt?: string; lastDiagnosticSummary?: string }>>('/api/devices/connectivity'),

  /** POST /api/devices/{id}/settings/typed/refresh — normalizes device typed settings from camera */
  normalizeDevice: (deviceId: string) =>
    request<Record<string, unknown>>(`/api/devices/${deviceId}/settings/typed/refresh`, { method: 'POST' }),

  /** POST /api/devices/{id}/probe — probes device capabilities */
  probeDevice: (deviceId: string) =>
    request<Record<string, unknown>>(`/api/devices/${deviceId}/probe`, { method: 'POST' }),

  /** POST /api/devices/{id}/settings/typed/apply — apply a single typed field */
  applyTypedField: (deviceId: string, fieldKey: string, value: unknown, expertOverride = false) =>
    request<WriteResult>(`/api/devices/${deviceId}/settings/typed/apply`, {
      method: 'POST',
      body: JSON.stringify({ fieldKey, value, expertOverride }),
    }),

  /** POST /api/devices/{id}/settings/typed/apply-batch — apply multiple typed fields */
  applyTypedBatch: (deviceId: string, changes: { fieldKey: string; value: unknown }[], expertOverride = false) =>
    request<WriteResult[]>(`/api/devices/${deviceId}/settings/typed/apply-batch`, {
      method: 'POST',
      body: JSON.stringify({ changes, expertOverride }),
    }),

  /** POST /api/devices/onvif/credential-scan — probe ONVIF device service with known defaults */
  onvifCredentialScan: (deviceId: string, ipAddress?: string) =>
    request<OnvifCredentialScanResult>('/api/devices/onvif/credential-scan', {
      method: 'POST',
      body: JSON.stringify({ deviceId, ipAddress }),
    }),

  /** GET /api/recovery/auto/status — autonomous camera-AP scan worker status */
  recoveryAutoStatus: () =>
    request<AutoRecoveryStatus>('/api/recovery/auto/status'),

  /** POST /api/devices/cgi-fuzz — fuzz known CGI endpoints for auth bypasses */
  cgiFuzz: (deviceId: string, ipAddress?: string, quickScan?: boolean) =>
    request<CgiFuzzResult>('/api/devices/cgi-fuzz', {
      method: 'POST',
      body: JSON.stringify({ deviceId, ipAddress, quickScan }),
    }),

  /** POST /api/devices/{id}/persistence/verify */
  persistenceVerify: (deviceId: string, endpoint: string, fieldKey?: string) =>
    request<PersistenceVerificationResult>(
      `/api/devices/${deviceId}/persistence/verify`,
      {
        method: 'POST',
        body: JSON.stringify({
          endpoint,
          fieldKey: fieldKey || undefined,
          method: 'GET',
          rebootForVerification: false,
        }),
      }
    ),

  // ── Camera Recovery (AP hotspot → LAN → Suite) ────────────────

  /** GET /api/recovery/scan — list factory-reset camera APs visible on the host WiFi */
  recoveryScan: () => request<{ aps: CameraApInfo[]; count: number }>('/api/recovery/scan'),

  /** POST /api/recovery/recover — start the background recover-and-enroll pipeline */
  recoveryStart: (serial: string, apSsid?: string) =>
    request<{ runId: string; serial: string }>('/api/recovery/recover', {
      method: 'POST',
      body: JSON.stringify({ serial, apSsid }),
    }),

  /** GET /api/recovery/status/{runId} — poll a running recovery */
  recoveryStatus: (runId: string) =>
    request<CameraRecoveryRunStatus>(`/api/recovery/status/${runId}`),
};
