using Boyfriend.Data;
using Boyfriend.Services;
using JetBrains.Annotations;
using Remora.Discord.API.Abstractions.Gateway.Events;
using Remora.Discord.Gateway.Responders;
using Remora.Results;

namespace Boyfriend.Responders;

/// <summary>
///     Handles updating <see cref="MemberData.Roles" /> when a guild member is updated.
/// </summary>
[UsedImplicitly]
public class GuildMemberUpdateResponder : IResponder<IGuildMemberUpdate>
{
    private readonly GuildDataService _guildData;

    public GuildMemberUpdateResponder(GuildDataService guildData)
    {
        _guildData = guildData;
    }

    public async Task<Result> RespondAsync(IGuildMemberUpdate gatewayEvent, CancellationToken ct = default)
    {
        var memberData = await _guildData.GetMemberData(gatewayEvent.GuildID, gatewayEvent.User.ID, ct);
        memberData.Roles = gatewayEvent.Roles.ToList().ConvertAll(r => r.Value);
        return Result.FromSuccess();
    }
}
