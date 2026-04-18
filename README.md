# BossCamSuite

Windows-first control suite and VMS scaffold for the 5523-w camera family.

Implemented surfaces in this repository:
- LAN NETSDK REST control adapter
- IPCamSuite private HTTP/CGI adapter
- EseeCloud app import and remote-command envelope adapter
- discovery providers for HiChip multicast, DVR broadcast, and ONVIF WS-Discovery
- SQLite-backed local inventory, audit log, capability cache, protocol manifest store, and firmware artifact catalog
- ASP.NET Core local service host and a WPF desktop shell

The protocol evidence loaded by the runtime lives under `assets/protocols`.
