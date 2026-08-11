/* BossCamSuite API TypeScript types — mirrors the .NET contracts used by BossCam.Service */

export interface DeviceIdentity {
  id: string;
  ipAddress: string | null;
  port: number;
  displayName: string | null;
  hardwareModel: string | null;
  deviceType: string | null;
  firmwareVersion: string | null;
  deviceId: string | null;
  eseeId: string | null;
  loginName: string | null;
  discoveredAt: string;
  transportProfiles: TransportProfile[];
  channelMap: DeviceChannelMap[];
  // Fleet identity fields (optional — older persisted records lack them)
  httpControlPort?: number;
  onvifMediaPort?: number | null;
  rtspPort?: number | null;
  lastGoodControlUrl?: string | null;
  lastGoodRtspUrl?: string | null;
  linkHint?: string | null; // 'Unknown' | 'Lan' | 'Wifi'
  continuousRecord?: boolean;
}

/** POST /api/devices/enroll — one-click enroll (probe → merge → optional continuous record) */
export interface EnrollDeviceRequest {
  ipAddress: string;
  port?: number;
  loginName?: string;
  password?: string;
  displayName?: string;
  hardwareModel?: string;
  credentialProfile?: string;
  startContinuousRecord?: boolean;
  linkHint?: string;
}

export interface EnrollStepResult {
  step: string;
  success: boolean;
  message?: string | null;
}

export interface EnrollDeviceResult {
  deviceId: string;
  ipAddress: string;
  enrolled: boolean;
  displayName?: string | null;
  hardwareModel?: string | null;
  httpControlPort: number;
  credentialProfile?: string | null;
  steps: EnrollStepResult[];
  chosenSourceUrl?: string | null;
  sourceRole?: string | null;
  degradedReason?: string | null;
  continuousJobId?: string | null;
}

export interface TransportProfile {
  name: string;
  kind: string;
  url: string;
}

export interface DeviceChannelMap {
  channelIndex: number;
  sourceUrl: string;
}

export interface VideoSourceDescriptor {
  url: string;
  displayName: string | null;
  kind: string;
  rank: number;
  metadata: Record<string, string>;
}

export type LiveMediaMode = 'HevcFmp4' | 'H264Fmp4' | 'H264MpegTs' | 'Mjpeg' | 'Snapshot';

export interface LiveMediaManifest {
  deviceId: string;
  sourceCodec: string;
  sourceRole: string;
  decisionReason: string;
  preferredMode: LiveMediaMode;
  fallbackModes: LiveMediaMode[];
  snapshotAvailable: boolean;
  mjpegUrl: string;
  h264Fmp4Url: string;
  hevcFmp4Url: string;
  mpegTsUrl: string;
  snapshotUrl: string;
}

export interface MediaStoragePaths {
  continuousRecordings: string;
  highlights: string;
  snapshots: string;
}

export interface RecordingJob {
  id: string;
  deviceId: string;
  profileId: string;
  sourceUrl: string;
  outputDirectory: string;
  segmentPattern: string;
  segmentSeconds: number;
  isRunning: boolean;
  lastError: string | null;
  processId: number | null;
  mode: string;
  sourceRole: string | null;
  degradedReason: string | null;
  startedAt: string;
  stoppedAt: string | null;
}

export interface RecordingSegment {
  id: string;
  deviceId: string;
  profileId: string;
  filePath: string;
  sizeBytes: number;
  durationSec: number;
  streamRole: string;
  container: string;
  hasAudio: boolean;
  jobId: string | null;
  startTime: string;
  endTime: string;
  indexedAt: string;
}

export interface HighlightState {
  selected: DeviceIdentity | null;
  selectedIndex: number;
  preferredStream: string;
  tiles: HighlightTile[];
}

/** Full highlight board state pushed via SignalR (from HighlightBoardState record). */
export interface HighlightBoardState {
  selectedDeviceId: string | null;
  selectedIndex: number;
  preferredStream: string;
  selected: HighlightTile | null;
  tiles: HighlightTile[];
}

/** A tile on the highlight board. */
export interface HighlightTile {
  deviceId: string;
  displayName: string;
  ipAddress: string;
  hardwareModel: string | null;
  channelName?: string | null;
  liveUrl?: string | null;
  snapshotUrl?: string | null;
  recordUrl?: string | null;
  mainRtspUrl?: string | null;
  subRtspUrl?: string | null;
  bubbleUrl?: string | null;
}

export interface HighlightTile {
  deviceId: string;
  displayName: string;
  ipAddress: string;
  hardwareModel: string | null;
}

export interface FirmwareArtifact {
  id: string;
  fileName: string;
  filePath: string;
  analyzedAt: string;
  metadata: Record<string, string>;
}

export interface UserAccount {
  username: string;
  role: string;
  enabled: boolean;
}

export interface PersistenceVerificationResult {
  id: string;
  deviceId: string;
  adapterName?: string;
  endpoint: string;
  immediateVerifyPassed?: boolean;
  rebootRequested?: boolean;
  rebootVerifyPassed?: boolean;
  immediateStatus?: string;
  persistenceStatus?: string;
  preValue?: unknown;
  postValue?: unknown;
  postRebootValue?: unknown;
  notes?: string;
  timestamp: string;
}

export interface HealthResponse {
  status: string;
  timestamp: string;
  platform: string;
  framework: string;
  processArch: string;
  contentRoot: string;
  ffmpeg: string | null;
  offlineMode: boolean;
  internetConnectivity: 'Unknown' | 'Online' | 'Offline' | 'Disabled';
  internetConnectivityChangedAt: string;
}

// Typed settings snapshot (minimal — mirrors backend TypedSettingGroupSnapshot)
export interface TypedSettingGroupSnapshot {
  deviceId: string;
  groupKind: string;
  groupName: string;
  fields: NormalizedSettingFieldSnapshot[];
}

export interface NormalizedSettingFieldSnapshot {
  fieldKey: string;
  displayName: string;
  typedValue: unknown;
  sourceEndpoint: string;
  writeVerified: boolean;
}

export interface WritePlan {
  endpoint: string;
  method: string;
  payload?: unknown;
  requireWriteVerification?: boolean;
  snapshotBeforeWrite?: boolean;
}

export interface FieldDef {
  key: string;
  label: string;
  type: 'string' | 'number' | 'bool';
  value: unknown;
  min?: number;
  max?: number;
}

// Control Point types (PR-T6: SPA feature-flag surface)
export interface ControlPointInventoryReport {
  deviceId: string;
  ipAddress: string;
  firmwareFingerprint: string;
  families: ControlPointInventoryFamily[];
  ambiguousControls: ControlPointInventoryItem[];
  capturedAt: string;
}

export interface ControlPointInventoryFamily {
  family: string;
  controls: ControlPointInventoryItem[];
}

export interface ControlPointInventoryItem {
  deviceId: string;
  firmwareFingerprint: string;
  family: string;
  contractKey: string;
  endpoint: string;
  wrapperObjectName: string;
  fieldKey: string;
  displayName: string;
  readWriteState: string;
  ownership: string;
  liveEvidence: string;
  primitiveType: string;
  controlType: string | null;
  traits: string[];
  allowedValues: string[];
  min: number | null;
  max: number | null;
  requiredFormat: string | null;
  valuesBounded: boolean;
  interFieldDependent: boolean;
  groupedWriteRequired: boolean;
  writeShape: string;
  recommendedWidget: string;
  existingWidget: string;
  existingWidgetMismatch: boolean;
  normalUiEligible: boolean;
  exactBlocker: string;
}

// Result of a typed field apply (mirrors C# WriteResult record)
export interface WriteResult {
  success: boolean;
  adapterName: string;
  message: string | null;
  statusCode: number | null;
  semanticStatus: string;
  contractKey: string | null;
  contractViolations: string[];
}

// ONVIF credential scan types (mirrors C# OnvifCredentialScanRequest / OnvifCredentialScanResult)
export interface OnvifCredentialScanRequest {
  deviceId?: string;
  ipAddress?: string;
}

export interface OnvifCredentialPair {
  username: string;
  password: string;
}

export interface OnvifCredentialScanResult {
  success: boolean;
  deviceServiceUrl: string | null;
  manufacturer: string | null;
  model: string | null;
  firmwareVersion: string | null;
  workingCredential: OnvifCredentialPair | null;
  attemptedCredentials: OnvifCredentialPair[];
  message: string | null;
}

// CGI fuzz types (mirrors C# CgiFuzzRequest / CgiFuzzResult / CgiFuzzFinding)
export interface CgiFuzzRequest {
  deviceId?: string;
  ipAddress?: string;
  quickScan?: boolean;
}

export interface CgiFuzzFinding {
  endpoint: string;
  method: string;
  variant: string;
  strategy: string | null;
  statusCode: number;
  contentType: string | null;
  bodyLength: number;
  bodyPreview: string | null;
  description: string | null;
}

export interface CgiFuzzResult {
  success: boolean;
  totalProbes: number;
  findings: CgiFuzzFinding[];
  gatedEndpoints: string[];
  message: string | null;
}

// Clip export request/result (mirrors C# ClipExportRequest / ClipExportResult)
export interface ClipExportRequest {
  deviceId: string;
  startTime: string;
  endTime: string;
  outputPath: string;
}

export interface ClipExportResult {
  success: boolean;
  outputPath: string;
  bytes: number;
  durationSec: number;
  reEncoded: boolean;
  message: string | null;
}
