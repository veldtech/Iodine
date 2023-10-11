using System.Buffers.Binary;
using System.Text;
using Remora.Discord.Caching.Abstractions;
using Remora.Discord.Caching.Abstractions.Services;
using Remora.Discord.Rest;
using Remora.Rest.Core;
using Remora.Results;

namespace Iodine.Services;

public class RedisTenantCacheWrapper(ICacheProvider _actual, ITokenStore _token) : ICacheProvider
{
    private readonly Snowflake _tokenID = ConvertTokenToSnowflake(_token.Token);

    public ValueTask CacheAsync<TInstance>(CacheKey key, TInstance instance, CacheEntryOptions options, CancellationToken ct = default)
        where TInstance : class => _actual.CacheAsync(CreateAppropriateCacheKey(_tokenID, key), instance, options, ct);

    public ValueTask<Result<TInstance>> RetrieveAsync<TInstance>(CacheKey key, CancellationToken ct = default)
        where TInstance : class => _actual.RetrieveAsync<TInstance>(CreateAppropriateCacheKey(_tokenID, key), ct);

    public ValueTask<Result> EvictAsync(CacheKey key, CancellationToken ct = default)
        => _actual.EvictAsync(CreateAppropriateCacheKey(_tokenID, key), ct);

    public ValueTask<Result<TInstance>> EvictAsync<TInstance>(CacheKey key, CancellationToken ct = default) 
        where TInstance : class => _actual.EvictAsync<TInstance>(CreateAppropriateCacheKey(_tokenID, key), ct);

    private static Snowflake ConvertTokenToSnowflake(string token) 
        => new(BinaryPrimitives.ReadUInt64LittleEndian(Convert.FromBase64String(token.Split('.')[0]).AsSpan()));

    private static CacheKey CreateAppropriateCacheKey(in Snowflake tenant, in CacheKey cacheKey)
    {
        var keyName = cacheKey.GetType().FullName!;

        if (keyName.Contains("Guild") || keyName.Contains("Message"))
        {
            // Security: Always return a tenant cache key for guilds and messages.
            // while a bot *could* theoretically be in the same server, under 90% of circumstances they aren't.
            return new TenantCacheKey(tenant, cacheKey);
        }

        return cacheKey;
    }
}

file record TenantCacheKey(Snowflake Tenant, CacheKey Inner) : CacheKey
{
    protected sealed override StringBuilder AppendToString(StringBuilder stringBuilder) 
        => stringBuilder.Append(Tenant).Append(':').Append(Inner.ToCanonicalString());
}