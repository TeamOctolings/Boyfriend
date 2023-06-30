using System.ComponentModel;
using System.Text;
using Boyfriend.Services;
using Boyfriend.Services.Data;
using Remora.Commands.Attributes;
using Remora.Commands.Groups;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.API.Objects;
using Remora.Discord.Commands.Conditions;
using Remora.Discord.Commands.Contexts;
using Remora.Discord.Commands.Extensions;
using Remora.Discord.Commands.Feedback.Services;
using Remora.Discord.Extensions.Embeds;
using Remora.Discord.Extensions.Formatting;
using Remora.Results;

// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedMember.Global

namespace Boyfriend.Commands;

/// <summary>
///     Handles commands related to mute management: /mute and /unmute.
/// </summary>
public class MuteCommandGroup : CommandGroup {
    private readonly IDiscordRestChannelAPI _channelApi;
    private readonly ICommandContext        _context;
    private readonly GuildDataService       _dataService;
    private readonly FeedbackService        _feedbackService;
    private readonly IDiscordRestGuildAPI   _guildApi;
    private readonly IDiscordRestUserAPI    _userApi;
    private readonly UtilityService         _utility;

    public MuteCommandGroup(
        ICommandContext context,         IDiscordRestChannelAPI channelApi, GuildDataService    dataService,
        FeedbackService feedbackService, IDiscordRestGuildAPI   guildApi,   IDiscordRestUserAPI userApi,
        UtilityService  utility) {
        _context = context;
        _channelApi = channelApi;
        _dataService = dataService;
        _feedbackService = feedbackService;
        _guildApi = guildApi;
        _userApi = userApi;
        _utility = utility;
    }

    /// <summary>
    ///     A slash command that mutes a Discord user with the specified reason.
    /// </summary>
    /// <param name="target">The user to mute.</param>
    /// <param name="duration">The duration for this mute. The user will be automatically unmuted after this duration.</param>
    /// <param name="reason">
    ///     The reason for this mute. Must be encoded with <see cref="Extensions.EncodeHeader" /> when passed to
    ///     <see cref="IDiscordRestGuildAPI.ModifyGuildMemberAsync" />.
    /// </param>
    /// <returns>
    ///     A feedback sending result which may or may not have succeeded. A successful result does not mean that the user
    ///     was muted and vice-versa.
    /// </returns>
    /// <seealso cref="UnmuteUserAsync" />
    [Command("mute", "мут")]
    [RequireContext(ChannelContext.Guild)]
    [RequireDiscordPermission(DiscordPermission.ModerateMembers)]
    [RequireBotDiscordPermissions(DiscordPermission.ModerateMembers)]
    [Description("мутит друга <3")]
    public async Task<Result> MuteUserAsync(
        [Description("друг которого нужно замутить ПОТОМУ-ЧТО ОН ЗАЕБАЛ")]
        IUser target,
        [Description("причина зачем мутить друга (пиши заебал)")]
        string reason,
        [Description("продолжительность мута")]
        TimeSpan duration) {
        // Data checks
        if (!_context.TryGetGuildID(out var guildId))
            return Result.FromError(new ArgumentNullError(nameof(guildId)));
        if (!_context.TryGetUserID(out var userId))
            return Result.FromError(new ArgumentNullError(nameof(userId)));
        if (!_context.TryGetChannelID(out var channelId))
            return Result.FromError(new ArgumentNullError(nameof(channelId)));

        // The current user's avatar is used when sending error messages
        var currentUserResult = await _userApi.GetCurrentUserAsync(CancellationToken);
        if (!currentUserResult.IsDefined(out var currentUser))
            return Result.FromError(currentUserResult);

        var memberResult = await _guildApi.GetGuildMemberAsync(guildId.Value, target.ID, CancellationToken);
        if (!memberResult.IsSuccess) {
            var embed = new EmbedBuilder().WithSmallTitle(Messages.UserNotFoundShort, currentUser)
                .WithColour(ColorsList.Red).Build();

            if (!embed.IsDefined(out var alreadyBuilt))
                return Result.FromError(embed);

            return (Result)await _feedbackService.SendContextualEmbedAsync(alreadyBuilt, ct: CancellationToken);
        }

        var interactionResult
            = await _utility.CheckInteractionsAsync(
                guildId.Value, userId.Value, target.ID, "Mute", CancellationToken);
        if (!interactionResult.IsSuccess)
            return Result.FromError(interactionResult);

        var data = await _dataService.GetData(guildId.Value, CancellationToken);
        var cfg = data.Configuration;
        Messages.Culture = data.Culture;

        Result<Embed> responseEmbed;
        if (interactionResult.Entity is not null) {
            responseEmbed = new EmbedBuilder().WithSmallTitle(interactionResult.Entity, currentUser)
                .WithColour(ColorsList.Red).Build();
        } else {
            var userResult = await _userApi.GetUserAsync(userId.Value, CancellationToken);
            if (!userResult.IsDefined(out var user))
                return Result.FromError(userResult);

            var until = DateTimeOffset.UtcNow.Add(duration); // >:)
            var muteResult = await _guildApi.ModifyGuildMemberAsync(
                guildId.Value, target.ID, reason: $"({user.GetTag()}) {reason}".EncodeHeader(),
                communicationDisabledUntil: until, ct: CancellationToken);
            if (!muteResult.IsSuccess)
                return Result.FromError(muteResult.Error);

            responseEmbed = new EmbedBuilder().WithSmallTitle(
                    string.Format(Messages.UserMuted, target.GetTag()), target)
                .WithColour(ColorsList.Green).Build();

            if ((cfg.PublicFeedbackChannel is not 0 && cfg.PublicFeedbackChannel != channelId.Value)
                || (cfg.PrivateFeedbackChannel is not 0 && cfg.PrivateFeedbackChannel != channelId.Value)) {
                var builder = new StringBuilder().AppendLine(string.Format(Messages.DescriptionActionReason, reason))
                    .Append(
                        string.Format(
                            Messages.DescriptionActionExpiresAt, Markdown.Timestamp(until)));

                var logEmbed = new EmbedBuilder().WithSmallTitle(
                        string.Format(Messages.UserMuted, target.GetTag()), target)
                    .WithDescription(builder.ToString())
                    .WithActionFooter(user)
                    .WithCurrentTimestamp()
                    .WithColour(ColorsList.Red)
                    .Build();

                if (!logEmbed.IsDefined(out var logBuilt))
                    return Result.FromError(logEmbed);

                var builtArray = new[] { logBuilt };
                // Not awaiting to reduce response time
                if (cfg.PublicFeedbackChannel != channelId.Value)
                    _ = _channelApi.CreateMessageAsync(
                        cfg.PublicFeedbackChannel.ToDiscordSnowflake(), embeds: builtArray,
                        ct: CancellationToken);
                if (cfg.PrivateFeedbackChannel != cfg.PublicFeedbackChannel
                    && cfg.PrivateFeedbackChannel != channelId.Value)
                    _ = _channelApi.CreateMessageAsync(
                        cfg.PrivateFeedbackChannel.ToDiscordSnowflake(), embeds: builtArray,
                        ct: CancellationToken);
            }
        }

        if (!responseEmbed.IsDefined(out var built))
            return Result.FromError(responseEmbed);

        return (Result)await _feedbackService.SendContextualEmbedAsync(built, ct: CancellationToken);
    }

    /// <summary>
    ///     A slash command that unmutes a Discord user with the specified reason.
    /// </summary>
    /// <param name="target">The user to unmute.</param>
    /// <param name="reason">
    ///     The reason for this unmute. Must be encoded with <see cref="Extensions.EncodeHeader" /> when passed to
    ///     <see cref="IDiscordRestGuildAPI.ModifyGuildMemberAsync" />.
    /// </param>
    /// <returns>
    ///     A feedback sending result which may or may not have succeeded. A successful result does not mean that the user
    ///     was unmuted and vice-versa.
    /// </returns>
    /// <seealso cref="MuteUserAsync" />
    /// <seealso cref="GuildUpdateService.TickGuildAsync"/>
    [Command("unmute", "размут")]
    [RequireContext(ChannelContext.Guild)]
    [RequireDiscordPermission(DiscordPermission.ModerateMembers)]
    [RequireBotDiscordPermissions(DiscordPermission.ModerateMembers)]
    [Description("ФУНКЦИЯ ФОРС РАЗМУТ КАВАЯ КОЛЕСИКИ!!!!1111111111")]
    public async Task<Result> UnmuteUserAsync(
        [Description("юзер кого раззамучивать")]
        IUser target,
        [Description("причина зачем раззамучивать")]
        string reason) {
        if (!_context.TryGetContextIDs(out var guildId, out var channelId, out var userId))
            return Result.FromError(
                new ArgumentNullError(nameof(_context), "Unable to retrieve necessary IDs from command context"));

        // The current user's avatar is used when sending error messages
        var currentUserResult = await _userApi.GetCurrentUserAsync(CancellationToken);
        if (!currentUserResult.IsDefined(out var currentUser))
            return Result.FromError(currentUserResult);

        var cfg = await _dataService.GetConfiguration(guildId.Value, CancellationToken);
        Messages.Culture = cfg.GetCulture();

        var memberResult = await _guildApi.GetGuildMemberAsync(guildId.Value, target.ID, CancellationToken);
        if (!memberResult.IsSuccess) {
            var embed = new EmbedBuilder().WithSmallTitle(Messages.UserNotFoundShort, currentUser)
                .WithColour(ColorsList.Red).Build();

            if (!embed.IsDefined(out var alreadyBuilt))
                return Result.FromError(embed);

            return (Result)await _feedbackService.SendContextualEmbedAsync(alreadyBuilt, ct: CancellationToken);
        }

        var interactionResult
            = await _utility.CheckInteractionsAsync(
                guildId.Value, userId.Value, target.ID, "Unmute", CancellationToken);
        if (!interactionResult.IsSuccess)
            return Result.FromError(interactionResult);

        // Needed to get the tag and avatar
        var userResult = await _userApi.GetUserAsync(userId.Value, CancellationToken);
        if (!userResult.IsDefined(out var user))
            return Result.FromError(userResult);

        var unmuteResult = await _guildApi.ModifyGuildMemberAsync(
            guildId.Value, target.ID, $"({user.GetTag()}) {reason}".EncodeHeader(),
            communicationDisabledUntil: null, ct: CancellationToken);
        if (!unmuteResult.IsSuccess)
            return Result.FromError(unmuteResult.Error);

        var responseEmbed = new EmbedBuilder().WithSmallTitle(
                string.Format(Messages.UserUnmuted, target.GetTag()), target)
            .WithColour(ColorsList.Green).Build();

        if ((cfg.PublicFeedbackChannel is not 0 && cfg.PublicFeedbackChannel != channelId.Value)
            || (cfg.PrivateFeedbackChannel is not 0 && cfg.PrivateFeedbackChannel != channelId.Value)) {
            var logEmbed = new EmbedBuilder().WithSmallTitle(
                    string.Format(Messages.UserUnmuted, target.GetTag()), target)
                .WithDescription(string.Format(Messages.DescriptionActionReason, reason))
                .WithActionFooter(user)
                .WithCurrentTimestamp()
                .WithColour(ColorsList.Green)
                .Build();

            if (!logEmbed.IsDefined(out var logBuilt))
                return Result.FromError(logEmbed);

            var builtArray = new[] { logBuilt };

            // Not awaiting to reduce response time
            if (cfg.PublicFeedbackChannel != channelId.Value)
                _ = _channelApi.CreateMessageAsync(
                    cfg.PublicFeedbackChannel.ToDiscordSnowflake(), embeds: builtArray,
                    ct: CancellationToken);
            if (cfg.PrivateFeedbackChannel != cfg.PublicFeedbackChannel
                && cfg.PrivateFeedbackChannel != channelId.Value)
                _ = _channelApi.CreateMessageAsync(
                    cfg.PrivateFeedbackChannel.ToDiscordSnowflake(), embeds: builtArray,
                    ct: CancellationToken);
        }

        if (!responseEmbed.IsDefined(out var built))
            return Result.FromError(responseEmbed);

        return (Result)await _feedbackService.SendContextualEmbedAsync(built, ct: CancellationToken);
    }
}