-- Check all key types before the first mutation: script errors do not roll back prior writes.
local expectedTypes = {'zset', 'hash', 'hash', 'string'}
for i = 1, 4 do
  local actual = redis.call('TYPE', KEYS[i]).ok
  if actual ~= 'none' and actual ~= expectedTypes[i] then
    return redis.error_reply('FF_FEATURE_KEY_TYPE')
  end
end
local cached = redis.call('GET', KEYS[4])
if cached then
  local valid, snapshot = pcall(cjson.decode, cached)
  if not valid or type(snapshot) ~= 'table' or snapshot.version ~= 2
      or type(snapshot.values) ~= 'string' or type(snapshot.digest) ~= 'string' then
    return redis.error_reply('FF_SNAPSHOT_FORMAT_CONFLICT')
  end
  if snapshot.digest ~= ARGV[6] then
    return redis.error_reply('FF_SNAPSHOT_INPUT_CONFLICT')
  end
  return snapshot.values
end
local tm = redis.call('TIME')
local now = tonumber(tm[1]) * 1000 + math.floor(tonumber(tm[2]) / 1000)
local tx = ARGV[1]
local amount = tonumber(ARGV[2])
local occurred = tonumber(ARGV[3])
local lat = tonumber(ARGV[4])
local lon = tonumber(ARGV[5])
redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - 3600000)
redis.call('ZADD', KEYS[1], now, tx)
local one = redis.call('ZCOUNT', KEYS[1], '(' .. (now - 60000), '+inf')
local five = redis.call('ZCOUNT', KEYS[1], '(' .. (now - 300000), '+inf')
local hour = redis.call('ZCARD', KEYS[1])
local prior = redis.call('HMGET', KEYS[2], 'lat', 'lon', 'occurred')
local priorLat = tonumber(prior[1]) or 0
local priorLon = tonumber(prior[2]) or 0
local priorTime = tonumber(prior[3]) or 0
local baseline = redis.call('HMGET', KEYS[3], 'n', 'mean', 'm2')
local n = tonumber(baseline[1]) or 0
local mean = tonumber(baseline[2]) or 0
local m2 = tonumber(baseline[3]) or 0
local late = 0
if occurred < priorTime then late = 1 end
local result = cjson.encode({one, five, hour, priorLat, priorLon, priorTime, n, mean, m2, late})
if occurred >= priorTime then
  redis.call('HSET', KEYS[2], 'lat', lat, 'lon', lon, 'occurred', occurred)
end
local delta = amount - mean
local nextMean = mean + delta / (n + 1)
redis.call('HSET', KEYS[3], 'n', n + 1, 'mean', nextMean, 'm2', m2 + delta * (amount - nextMean))
redis.call('EXPIRE', KEYS[1], 3700)
redis.call('EXPIRE', KEYS[2], 172800)
redis.call('EXPIRE', KEYS[3], 7776000)
redis.call('SET', KEYS[4], cjson.encode({version = 2, digest = ARGV[6], values = result}), 'EX', 172800)
return result
