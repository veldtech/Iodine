using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Remora.Discord.API;
using Remora.Discord.Caching.Abstractions;
using Remora.Discord.Caching.Abstractions.Services;
using Remora.Discord.Rest;
using Remora.Rest.Core;
using Remora.Results;

namespace Iodine.Services;

public class RedisTentantCacheWrapper(ICacheProvider _actual, ITokenStore _token) : ICacheProvider
{
    private readonly Snowflake _tokenID = ConvertTokenToSnowflake(_token.Token);

    public ValueTask CacheAsync<TInstance>(CacheKey key, TInstance instance, CacheEntryOptions options, CancellationToken ct = default)
        where TInstance : class => _actual.CacheAsync(new TenantCacheKey(_tokenID, key), instance, options, ct);

    public ValueTask<Result<TInstance>> RetrieveAsync<TInstance>(CacheKey key, CancellationToken ct = default)
        where TInstance : class => _actual.RetrieveAsync<TInstance>(new TenantCacheKey(_tokenID, key), ct);

    public ValueTask<Result> EvictAsync(CacheKey key, CancellationToken ct = default)
        => _actual.EvictAsync(new TenantCacheKey(_tokenID, key), ct);

    public ValueTask<Result<TInstance>> EvictAsync<TInstance>(CacheKey key, CancellationToken ct = default) 
        where TInstance : class => _actual.EvictAsync<TInstance>(new TenantCacheKey(_tokenID, key), ct);

    private static Snowflake ConvertTokenToSnowflake(string token) 
        => new(BinaryPrimitives.ReadUInt64LittleEndian(Convert.FromBase64String(token.Split('.')[0]).AsSpan()));
}

file record TenantCacheKey(Snowflake Tenant, CacheKey Inner) : CacheKey
{
    protected sealed override StringBuilder AppendToString(StringBuilder stringBuilder) 
        => stringBuilder.Append(Tenant).Append(':').Append(Inner.ToCanonicalString());
}