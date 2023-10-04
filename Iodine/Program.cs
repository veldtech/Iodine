using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.HighPerformance;
using CommunityToolkit.HighPerformance.Buffers;
using Iodine.PolyfillHelper;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Polly;
using Remora.Discord.API;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.Caching;
using Remora.Discord.Caching.Abstractions;
using Remora.Discord.Caching.Abstractions.Services;
using Remora.Discord.Caching.Redis.Extensions;
using Remora.Discord.Caching.Redis.Services;
using Remora.Discord.Caching.Services;
using Remora.Discord.Rest;
using Remora.Discord.Rest.Extensions;
using Remora.Rest;
using Remora.Rest.Core;
using Remora.Rest.Json;
using Remora.Rest.Results;
using Remora.Results;
using StackExchange.Redis;
using Constants = Remora.Discord.API.Constants;

var builder = WebApplication.CreateSlimBuilder(args);

var services = builder.Services;

services.AddDiscordRest(s => (s.GetRequiredService<IConfiguration>()["Discord:Token"]!, DiscordTokenType.Bot));
services.AddOptions<CacheSettings>();
services.AddSingleton<ImmutableCacheSettings>();

services.AddStackExchangeRedisCache
(
    options => options.ConfigurationOptions = new ConfigurationOptions
    {
        EndPoints = { builder.Configuration["Redis:Url"] },
    }
);

services.TryAddSingleton<RedisCacheProvider>();
services.AddSingleton<ICacheProvider>(s => s.GetRequiredService<RedisCacheProvider>());

services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new SnowflakeConverter(Constants.DiscordEpoch)));

var app = builder.Build();

app.MapGet
(
    "/channels/{channelID}/messages/{messageID}",
    async (HttpContext context, IRestHttpClient rest, ICacheProvider cache, Snowflake channelID, Snowflake messageID) =>
    {
        var cacheResult = await cache.RetrieveAsync<IMessage>(new KeyHelpers.MessageCacheKey(channelID, messageID));

        if (!cacheResult.IsSuccess)
        {
            var fetch = await rest.GetAsync<IMessage>($"channels/{channelID}/messages/{messageID}");

            if (!fetch.IsSuccess)
            {
                await HandleRestErrorAsync(context, fetch.Error);

                return;
            }

            await RespondAsync(context, fetch.Entity);
        }
    }
);

app.MapPost
(
    "/channels/{channelID}/messages/", 
    async (HttpContext context, IDiscordRestChannelAPI rest, ICacheProvider cache, Snowflake channelID) =>
    {
        var jsonOptions = context.RequestServices.GetRequiredService<IOptionsMonitor<JsonSerializerOptions>>().Get("Discord");
        var message = await GetJsonNodesFromRequestAsync(context);

        var postResult = await rest.CreateMessageAsync
        (
            channelID,
            message["content"].Deserialize<Optional<string>>(jsonOptions),
            embeds: message["embeds"].Deserialize<Optional<IReadOnlyList<IEmbed>>>(jsonOptions),
            allowedMentions: message["allowed_mentions"].Deserialize<Optional<IAllowedMentions>>(jsonOptions),
            messageReference: message["message_reference"].Deserialize<Optional<IMessageReference>>(jsonOptions),
            components: message["components"].Deserialize<Optional<IReadOnlyList<IMessageComponent>>>(jsonOptions)
        );

        if (!postResult.IsSuccess)
        {
            await HandleRestErrorAsync(context, postResult.Error);

            return;
        }

        await RespondAsync(context, postResult.Entity);

        await CacheAsync(context, cache, postResult.Entity, new KeyHelpers.MessageCacheKey(channelID, postResult.Entity.ID));
    }
);

app.Run();

return;

static async Task CacheAsync<T>(HttpContext context, ICacheProvider provider, T entity, CacheKey cacheKey) where T : class
{
    var cacheEntity = entity;
    var existsResult = await provider.RetrieveAsync<T>(cacheKey);
    var cacheOptions = context.RequestServices.GetRequiredService<ImmutableCacheSettings>();
    
    if (existsResult.IsSuccess)
    {
        cacheEntity = CacheHelper<T>.FastPolyFill(existsResult.Entity, entity);
    }
    
    await provider.CacheAsync(cacheKey, cacheEntity, cacheOptions.GetEntryOptions<T>());
}

static async Task<JsonNode> GetJsonNodesFromRequestAsync(HttpContext context)
{
    using var bufferWriter = new ArrayPoolBufferWriter<byte>();
    await using var bufferStream = bufferWriter.AsStream();
    
    await context.Request.Body.CopyToAsync(bufferStream);

    var content = JsonNode.Parse(bufferWriter.WrittenSpan);

    return content;
}

static async Task HandleRestErrorAsync(HttpContext httpContext, IResultError fetchError)
{
    var (statusCode, data) = fetchError switch
    {
        RestResultError<IRestError> re => ((int)HttpStatusCode.BadRequest, re.Message),
        HttpResultError http           => ((int)http.StatusCode, http.Message),
        _                              => throw new UnreachableException("The rest client only returns two types of errors."),
    };

    httpContext.Response.StatusCode = statusCode;
    await httpContext.Response.WriteAsJsonAsync(new { error = data });
}

static async Task RespondAsync<TValue>(HttpContext context, TValue payload)
{
    // This is very fast and very efficient. See links for info:
    // https://github.com/Remora/Remora.Discord/pull/318#issuecomment-1745328751
    // https://github.com/Remora/Remora.Discord/blob/434c68a/Backend/Remora.Discord.Gateway/Transport/WebSocketPayloadTransportService.cs#L170-L179
    
    var jsonOptions = context.RequestServices.GetRequiredService<IOptionsMonitor<JsonSerializerOptions>>().Get("Discord");
    
    using var bufferWriter = new ArrayPoolBufferWriter<byte>(256);
    await using var jsonWriter = new Utf8JsonWriter(bufferWriter, new JsonWriterOptions { Encoder = jsonOptions.Encoder, SkipValidation = !Debugger.IsAttached });

    JsonSerializer.Serialize(jsonWriter, payload, jsonOptions);

    bufferWriter.WrittenSpan.CopyTo(context.Response.BodyWriter.GetSpan(bufferWriter.WrittenCount));
}