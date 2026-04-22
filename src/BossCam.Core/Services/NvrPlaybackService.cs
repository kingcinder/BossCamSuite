using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class NvrPlaybackService(
    IEnumerable<IControlAdapter> controlAdapters,
    IApplicationStore store,
    ILogger<NvrPlaybackService> logger)
{
    public Task<NvrPlaybackCallResult?> FindFileAsync(Guid deviceId, NvrPlaybackRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(deviceId, "FindFile", "/NetSDK/SDCard/media/search", request, cancellationToken);

    public Task<NvrPlaybackCallResult?> FindNextFileAsync(Guid deviceId, NvrPlaybackRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(deviceId, "FindNextFile", "/NetSDK/SDCard/media/search", request, cancellationToken);

    public Task<NvrPlaybackCallResult?> GetFileByTimeAsync(Guid deviceId, NvrPlaybackRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(deviceId, "GetFileByTime", "/NetSDK/SDCard/media/search", request, cancellationToken);

    public Task<NvrPlaybackCallResult?> PlayBackByTimeExAsync(Guid deviceId, NvrPlaybackRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(deviceId, "PlayBackByTimeEx", "/NetSDK/SDCard/media/playbackFLV", request, cancellationToken);

    public Task<NvrPlaybackCallResult?> FindCloseAsync(Guid deviceId, NvrPlaybackRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(deviceId, "FindClose", "/NetSDK/SDCard/media/search", request, cancellationToken);

    public Task<NvrPlaybackCallResult?> PlayBackByNameAsync(Guid deviceId, NvrPlaybackRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(deviceId, "PlayBackByName", "/NetSDK/SDCard/media/playbackFLV", request, cancellationToken);

    public Task<NvrPlaybackCallResult?> GetFileByNameAsync(Guid deviceId, NvrPlaybackRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(deviceId, "GetFileByName", "/NetSDK/SDCard/media/search", request, cancellationToken);

    public Task<NvrPlaybackCallResult?> StopGetFileAsync(Guid deviceId, NvrPlaybackRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(deviceId, "StopGetFile", "/NetSDK/SDCard/media/search", request, cancellationToken);

    public Task<NvrPlaybackCallResult?> PlayBackSaveDataAsync(Guid deviceId, NvrPlaybackRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(deviceId, "PlayBackSaveData", "/NetSDK/SDCard/media/playbackFLV", request, cancellationToken);

    public Task<NvrPlaybackCallResult?> StopPlayBackSaveAsync(Guid deviceId, NvrPlaybackRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(deviceId, "StopPlayBackSave", "/NetSDK/SDCard/media/playbackFLV", request, cancellationToken);

    private async Task<NvrPlaybackCallResult?> ExecuteAsync(
        Guid deviceId,
        string operation,
        string endpointPath,
        NvrPlaybackRequest request,
        CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var adapter = await ResolveAdapterAsync(device, cancellationToken);
        if (adapter is null)
        {
            return new NvrPlaybackCallResult
            {
                Success = false,
                Operation = operation,
                AdapterName = string.Empty,
                Endpoint = endpointPath,
                Message = "No control adapter matched the target device."
            };
        }

        var query = BuildQuery(operation, request);
        var endpoint = BuildEndpoint(endpointPath, query);
        var write = await adapter.ApplyAsync(device, new WritePlan
        {
            AdapterName = adapter.Name,
            GroupName = "Storage",
            Endpoint = endpoint,
            Method = "GET",
            SnapshotBeforeWrite = false,
            RequireWriteVerification = false
        }, cancellationToken);

        var call = new NvrPlaybackCallResult
        {
            Success = write.Success,
            Operation = operation,
            AdapterName = adapter.Name,
            Endpoint = endpoint,
            Method = "GET",
            StatusCode = write.StatusCode,
            Message = write.Message,
            Response = write.Response?.DeepClone(),
            Query = query
        };

        await store.AddAuditEntryAsync(new WriteAuditEntry
        {
            DeviceId = device.Id,
            AdapterName = adapter.Name,
            Operation = operation,
            Endpoint = endpoint,
            RequestContent = JsonSerializer.Serialize(query),
            ResponseContent = call.Response?.ToJsonString(),
            Success = call.Success,
            SemanticStatus = call.Success ? SemanticWriteStatus.AcceptedChanged : SemanticWriteStatus.Rejected,
            BlockReason = call.Success ? null : call.Message
        }, cancellationToken);

        return call;
    }

    private async Task<IControlAdapter?> ResolveAdapterAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        foreach (var adapter in controlAdapters.OrderBy(static adapter => adapter.Priority))
        {
            try
            {
                if (await adapter.CanHandleAsync(device, cancellationToken))
                {
                    return adapter;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Playback adapter resolution failed for {Adapter} on {Device}", adapter.Name, device.DisplayName);
            }
        }

        return null;
    }

    private static Dictionary<string, string> BuildQuery(string operation, NvrPlaybackRequest request)
    {
        var beginUtc = request.BeginTime.ToUnixTimeSeconds();
        var endUtc = request.EndTime.ToUnixTimeSeconds();
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sessionID"] = Math.Max(0, request.SessionId).ToString(CultureInfo.InvariantCulture),
            ["channelID"] = Math.Max(1, request.ChannelId).ToString(CultureInfo.InvariantCulture),
            ["beginUTC"] = beginUtc.ToString(CultureInfo.InvariantCulture),
            ["endUTC"] = endUtc.ToString(CultureInfo.InvariantCulture),
            ["beginTime"] = beginUtc.ToString(CultureInfo.InvariantCulture),
            ["endTime"] = endUtc.ToString(CultureInfo.InvariantCulture),
            ["type"] = string.IsNullOrWhiteSpace(request.Type) ? "all" : request.Type.Trim()
        };

        if (operation.Equals("FindNextFile", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(request.Cursor))
        {
            query["cursor"] = request.Cursor.Trim();
            query["next"] = request.Cursor.Trim();
        }

        if (operation.Equals("FindClose", StringComparison.OrdinalIgnoreCase))
        {
            query["action"] = "close";
            query["findHandle"] = (request.HandleId ?? request.SessionId).ToString(CultureInfo.InvariantCulture);
        }

        if (operation.Equals("PlayBackByName", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(request.FileName))
            {
                query["fileName"] = request.FileName.Trim();
                query["playFile"] = request.FileName.Trim();
            }
        }

        if (operation.Equals("GetFileByName", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(request.FileName))
            {
                query["fileName"] = request.FileName.Trim();
                query["dvrFileName"] = request.FileName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.SavePath))
            {
                query["savePath"] = request.SavePath.Trim();
                query["savedFileName"] = request.SavePath.Trim();
            }
        }

        if (operation.Equals("StopGetFile", StringComparison.OrdinalIgnoreCase))
        {
            query["action"] = "stopDownload";
            if (request.HandleId is int handle)
            {
                query["fileHandle"] = handle.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (operation.Equals("PlayBackSaveData", StringComparison.OrdinalIgnoreCase))
        {
            query["action"] = "save";
            if (!string.IsNullOrWhiteSpace(request.SavePath))
            {
                query["savePath"] = request.SavePath.Trim();
                query["fileName"] = request.SavePath.Trim();
            }
        }

        if (operation.Equals("StopPlayBackSave", StringComparison.OrdinalIgnoreCase))
        {
            query["action"] = "stopSave";
            if (request.HandleId is int handle)
            {
                query["playHandle"] = handle.ToString(CultureInfo.InvariantCulture);
            }
        }

        return query;
    }

    private static string BuildEndpoint(string endpointPath, IReadOnlyDictionary<string, string> query)
    {
        var encoded = string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return string.IsNullOrWhiteSpace(encoded) ? endpointPath : $"{endpointPath}?{encoded}";
    }
}
