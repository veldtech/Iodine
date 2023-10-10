using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.HighPerformance;
using CommunityToolkit.HighPerformance.Buffers;
using Iodine.PolyfillHelper;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Remora.Discord.API;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.API.Objects;
using Remora.Discord.Caching;
using Remora.Discord.Caching.Abstractions;
using Remora.Discord.Caching.Abstractions.Services;
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

// This is what will thunk API requests
app.MapFallback
(
    async (HttpContext context, IRestHttpClient rest, ICacheProvider cache) =>
    {
        var restResult = await (context.Request.Method switch
        {
            "GET"    => rest.GetAsync<JsonNode>(context.Request.Path, b => b.WithRateLimitContext(cache)),
            "PUT"    => rest.PutAsync<JsonNode>(context.Request.Path, b => b.WithRateLimitContext(cache)),
            "POST"   => rest.PostAsync<JsonNode>(context.Request.Path, b => b.WithRateLimitContext(cache)),
            "PATCH"  => rest.PatchAsync<JsonNode>(context.Request.Path, b => b.WithRateLimitContext(cache)),
            "DELETE" => rest.DeleteAsync<JsonNode>(context.Request.Path, b => b.WithRateLimitContext(cache)),
            _        => Task.FromResult(Result<JsonNode>.FromError(new InvalidOperationError("Unknown request method"))),
        });

        if (restResult.IsSuccess)
        {
            await ResponseHelper<JsonNode>.RespondAsync(context, restResult.Entity);
        }
        else
        {
            await ResponseHelper<JsonNode>.HandleRestErrorAsync(context, restResult.Error);
        }

    }
);

// Gets a message from the API.
app.MapGet
(
    "/channels/{channelID}/messages/{messageID}",
    (HttpContext context, IRestHttpClient rest, ICacheProvider cache, ulong channelID, ulong messageID) 
        => ResponseHelper<IMessage>.HandleRequestAsync
        (
            context,
            rest,
            cache,
            static (IMessage? _, (ulong cid, ulong mid) state) => new KeyHelpers.MessageCacheKey(new(state.cid), new(state.mid)),
            (channelID, messageID)
        )
);

// Posts a message to a channel, caching the result.
app.MapPost
(
    "/channels/{channelID}/messages/",
    (HttpContext context, IRestHttpClient rest, ICacheProvider cache, ulong channelID)
        => ResponseHelper<IMessage>.HandleRequestAsync
        (
            context,
            rest,
            cache,
            static (message, cid) => new KeyHelpers.MessageCacheKey(message!.ChannelID, message.ID),
            channelID
        )
);

app.MapPatch
(
    "/channels/{channelID}/messages/{messageID}",
    (HttpContext context, IRestHttpClient rest, ICacheProvider cache, ulong channelID, ulong messageID)
        => ResponseHelper<IMessage>.HandleRequestAsync
        (
            context,
            rest,
            cache,
            static (IMessage? _, (ulong cid, ulong mid) state) => new KeyHelpers.MessageCacheKey(new(state.cid), new(state.mid)),
            (channelID, messageID)
        )
);

app.MapDelete
(
    "/channels/{channelID}/messages/{messageID}",
    (HttpContext context, IRestHttpClient rest, ICacheProvider cache, ulong channelID, ulong messageID)
        => ResponseHelper<IMessage>.HandleRequestAsync
        (
            context,
            rest,
            cache,
            static (IMessage? _, (ulong cid, ulong mid) state) => new KeyHelpers.MessageCacheKey(new(state.cid), new(state.mid)),
            (channelID, messageID)
        )
);

app.Run();

return;

// A generic helper method that encapsulates common logic between endpoints.
file static class ResponseHelper<TEntity> where TEntity : class
{
    public static async Task HandleRequestAsync<TState> 
    (
        HttpContext context,
        IRestHttpClient rest,
        ICacheProvider cache,
        Func<TEntity?, TState?, CacheKey> cacheKey,
        TState? state = default
    )
    {
        var requestMethod = context.Request.Method;

        if (requestMethod is "GET")
        {
            var cacheResult = await cache.RetrieveAsync<TEntity>(cacheKey(null, state));

            if (cacheResult.IsSuccess)
            {
                await RespondAsync(context, cacheResult.Entity);

                return;
            }
        }
    
        var restResult = await (requestMethod switch
        {
            "GET"    => rest.GetAsync<TEntity>(context.Request.Path, b => b.WithRateLimitContext(cache)),
            "PUT"    => rest.PutAsync<TEntity>(context.Request.Path, b => b.WithRateLimitContext(cache), allowNullReturn: true),
            "POST"   => rest.PostAsync<TEntity>(context.Request.Path, b => b.WithRateLimitContext(cache)),
            "PATCH"  => rest.PatchAsync<TEntity>(context.Request.Path, b => b.WithRateLimitContext(cache), allowNullReturn: true),
            "DELETE" => rest.DeleteAsync<TEntity>(context.Request.Path, b => b.WithRateLimitContext(cache), allowNullReturn: true),
            _        => Task.FromResult(Result<TEntity>.FromError(new InvalidOperationError("Unknown request method"))),
        });

        if (!restResult.IsDefined(out var res))
        {
            await HandleRestErrorAsync(context, restResult.Error!);

            return;
        }

        await RespondAsync(context, res);

        if (requestMethod is "DELETE")
        {
            await cache.EvictAsync(cacheKey(null, state));
        }
        else
        {
            var cacheResult = await cache.RetrieveAsync<TEntity>(cacheKey(res, state));

            TEntity? cacheEntity = !cacheResult.IsDefined(out cacheEntity) ? res : CacheHelper<TEntity>.FastPolyFill(res, cacheEntity);
            await CacheAsync(context, cache, cacheEntity, cacheKey(res, state));
        }
    }
    
    static async Task CacheAsync(HttpContext context, ICacheProvider provider, TEntity entity, CacheKey cacheKey)
    {
        var cacheEntity = entity;
        var existsResult = await provider.RetrieveAsync<TEntity>(cacheKey);
        var cacheOptions = context.RequestServices.GetRequiredService<ImmutableCacheSettings>();
        
        if (existsResult.IsSuccess)
        {
            cacheEntity = CacheHelper<TEntity>.FastPolyFill(existsResult.Entity, entity);
        }
        
        await provider.CacheAsync(cacheKey, cacheEntity, cacheOptions.GetEntryOptions<TEntity>());
    }

    public static async Task<JsonNode> GetJsonNodesFromRequestAsync(HttpContext context)
    {
        using var bufferWriter = new ArrayPoolBufferWriter<byte>();
        await using var bufferStream = bufferWriter.AsStream();
        
        await context.Request.Body.CopyToAsync(bufferStream);

        var content = JsonNode.Parse(bufferWriter.WrittenSpan)!;

        return content;
    }

    public static async Task HandleRestErrorAsync(HttpContext httpContext, IResultError fetchError)
    {
        var (statusCode, data) = fetchError switch
        {
            RestResultError<RestError> re => ((int)HttpStatusCode.BadRequest, re.Message),
            HttpResultError http           => ((int)http.StatusCode, http.Message),
            _                              => throw new UnreachableException("The rest client only returns two types of errors."),
        };

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(new { error = data });
    }

    public static async Task RespondAsync<TValue>(HttpContext context, TValue payload)
    {
        // This is very fast and very efficient. See links for info:
        // https://github.com/Remora/Remora.Discord/pull/318#issuecomment-1745328751
        // https://github.com/Remora/Remora.Discord/blob/434c68a/Backend/Remora.Discord.Gateway/Transport/WebSocketPayloadTransportService.cs#L170-L179
        
        var jsonOptions = context.RequestServices.GetRequiredService<IOptionsMonitor<JsonSerializerOptions>>().Get("Discord");
        
        using var bufferWriter = new ArrayPoolBufferWriter<byte>(256);
        await using var jsonWriter = new Utf8JsonWriter(bufferWriter, new JsonWriterOptions { Encoder = jsonOptions.Encoder, SkipValidation = !Debugger.IsAttached });

        if (payload is JsonNode jn) // Our fallback calls into ResponseHelper<JsonNode>
        {
            jn.WriteTo(jsonWriter, jsonOptions);
        }
        else
        {
            
            JsonSerializer.Serialize(jsonWriter, payload, jsonOptions);
        }

        bufferWriter.WrittenSpan.CopyTo(context.Response.BodyWriter.GetSpan(bufferWriter.WrittenCount));
    }
}