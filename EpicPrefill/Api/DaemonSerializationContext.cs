using System.Text.Json.Serialization;

namespace EpicPrefill.Api;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CommandRequest))]
[JsonSerializable(typeof(CommandResponse))]
[JsonSerializable(typeof(CredentialChallenge))]
[JsonSerializable(typeof(EncryptedCredentialResponse))]
[JsonSerializable(typeof(List<OwnedGame>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(PrefillResult))]
[JsonSerializable(typeof(StatusData))]
[JsonSerializable(typeof(PrefillProgressUpdate))]
[JsonSerializable(typeof(ClearCacheResult))]
[JsonSerializable(typeof(AppStatus))]
[JsonSerializable(typeof(SelectedAppsStatus))]
[JsonSerializable(typeof(CacheStatusResult))]
[JsonSerializable(typeof(AppCacheStatus))]
[JsonSerializable(typeof(CdnInfo))]
[JsonSerializable(typeof(CdnInfoResult))]
// Socket event types
[JsonSerializable(typeof(SocketEvent<CredentialChallenge>))]
[JsonSerializable(typeof(SocketEvent<PrefillProgressUpdate>))]
[JsonSerializable(typeof(SocketEvent<AuthStateData>))]
[JsonSerializable(typeof(AuthStateData))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(RunSnapshot))]
[JsonSerializable(typeof(RunItemSnapshot))]
[JsonSerializable(typeof(OperationPage))]
[JsonSerializable(typeof(PrefillStart))]
internal sealed partial class DaemonSerializationContext : JsonSerializerContext
{
}
