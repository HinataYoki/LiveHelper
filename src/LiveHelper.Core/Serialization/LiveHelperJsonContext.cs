using System.Text.Json.Serialization;
using LiveHelper.Core.Models;
using LiveHelper.Core.Services;

namespace LiveHelper.Core.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IgnoreReadOnlyProperties = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PersistedState))]
[JsonSerializable(typeof(WorkspacePointer))]
internal sealed partial class LiveHelperJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(BilibiliDanmakuService.BilibiliDanmakuRecord))]
[JsonSerializable(typeof(BilibiliDanmakuService.BilibiliAuthPacket))]
internal sealed partial class BilibiliJsonContext : JsonSerializerContext;
