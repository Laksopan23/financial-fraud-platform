#!/usr/bin/env python3
"""Execute the production Lua script with deterministic Redis/CJSON test doubles.

Requires a Lua 5.4 or 5.3 shared library. These tests exercise script control flow
and state transitions; Testcontainers is still required to verify Redis itself,
wire serialization, cancellation, concurrency and C# integration.
"""
from pathlib import Path
import ctypes
import ctypes.util
import os
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "src/Services/Detection/FinancialFraudPlatform.Detection.FeatureStore/Scripts/features.lua"
LUA_LIBRARY = ctypes.util.find_library("lua5.4") or ctypes.util.find_library("lua5.3")
if not LUA_LIBRARY and os.environ.get("FF_REQUIRE_LUA") == "1":
    raise RuntimeError("This gate requires the Lua 5.4/5.3 shared library")

HARNESS = r'''
local now = 1800000000000
local db, encoded = {}, {}
local serial, writes = 0, 0
local function copy(value)
  if type(value) ~= 'table' then return value end
  local output = {}
  for key, child in pairs(value) do output[key] = copy(child) end
  return output
end
-- CJSON is a serialization boundary double; no JSON fidelity is claimed here.
cjson = {}
function cjson.encode(value)
  serial = serial + 1
  local key = 'encoded:' .. serial
  encoded[key] = copy(value)
  return key
end
function cjson.decode(key)
  assert(encoded[key], 'invalid JSON')
  return copy(encoded[key])
end
local function entry(key)
  local value = db[key]
  if value and value.expires and value.expires <= now then db[key] = nil; return nil end
  return value
end
local function requireType(key, kind)
  local value = entry(key)
  assert(not value or value.kind == kind, 'WRONGTYPE')
  return value
end
local function create(key, kind)
  local value = requireType(key, kind)
  if not value then value = {kind=kind, value={}}; db[key] = value end
  return value
end
local function inRange(score, minimum, maximum)
  local function bound(text, lower)
    if text == '-inf' then return true end
    if text == '+inf' then return true end
    local exclusive = tostring(text):sub(1, 1) == '('
    local value = tonumber(exclusive and tostring(text):sub(2) or text)
    if lower then return exclusive and score > value or (not exclusive and score >= value) end
    return exclusive and score < value or (not exclusive and score <= value)
  end
  return bound(minimum, true) and bound(maximum, false)
end
redis = {}
function redis.error_reply(message) return {err=message} end
function redis.call(command, ...)
  local args = {...}
  local key = args[1]
  if command == 'TIME' then return {tostring(math.floor(now / 1000)), tostring((now % 1000) * 1000)} end
  if command == 'TYPE' then return {ok=entry(key) and entry(key).kind or 'none'} end
  if command == 'GET' then local value=requireType(key, 'string'); return value and value.value or false end
  if command == 'SET' then
    writes = writes + 1
    db[key] = {kind='string', value=args[2], expires=args[3] == 'EX' and now + args[4] * 1000 or nil}
    return 'OK'
  end
  if command == 'HMGET' then
    local value = requireType(key, 'hash')
    local result = {}
    for i=2,#args do result[i-1] = value and value.value[args[i]] or false end
    return result
  end
  if command == 'HSET' then
    writes = writes + 1
    local value = create(key, 'hash')
    for i=2,#args,2 do value.value[args[i]] = tostring(args[i+1]) end
    return 1
  end
  if command == 'ZADD' then
    writes = writes + 1
    create(key, 'zset').value[args[3]] = tonumber(args[2])
    return 1
  end
  if command == 'ZREMRANGEBYSCORE' then
    writes = writes + 1
    local value = requireType(key, 'zset')
    if value then
      for member, score in pairs(value.value) do
        if inRange(score, args[2], args[3]) then value.value[member] = nil end
      end
    end
    return 1
  end
  if command == 'ZCOUNT' or command == 'ZCARD' then
    local value = requireType(key, 'zset')
    local count = 0
    if value then
      for _, score in pairs(value.value) do
        if command == 'ZCARD' or inRange(score, args[2], args[3]) then count = count + 1 end
      end
    end
    return count
  end
  if command == 'EXPIRE' then
    writes = writes + 1
    if entry(key) then db[key].expires = now + args[2] * 1000 end
    return 1
  end
  error('Unsupported Redis command: ' .. command)
end
KEYS = {'velocity', 'geo', 'baseline', 'snapshot:one'}
ARGV = {'one', '100', tostring(now), '0', '0', 'digest:one'}
local production = assert(load(FEATURE_SCRIPT, 'features.lua'))
local function observe() return production() end
local function values() return cjson.decode(observe()) end
local function nextTransaction(id, milliseconds, latitude)
  now = now + milliseconds
  KEYS[4] = 'snapshot:' .. id
  ARGV = {id, '100', tostring(now), tostring(latitude or 0), '0', 'digest:' .. id}
end
'''


class LuaRuntime:
    def __init__(self):
        self.lib = ctypes.CDLL(LUA_LIBRARY)
        self.lib.luaL_newstate.restype = ctypes.c_void_p
        self.lib.luaL_openlibs.argtypes = [ctypes.c_void_p]
        self.lib.lua_pushlstring.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_size_t]
        self.lib.lua_pushlstring.restype = ctypes.c_void_p
        self.lib.lua_setglobal.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        self.lib.luaL_loadbufferx.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p, ctypes.c_char_p]
        self.lib.luaL_loadbufferx.restype = ctypes.c_int
        self.lib.lua_pcallk.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_ssize_t, ctypes.c_void_p]
        self.lib.lua_pcallk.restype = ctypes.c_int
        self.lib.lua_tolstring.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.POINTER(ctypes.c_size_t)]
        self.lib.lua_tolstring.restype = ctypes.c_void_p
        self.lib.lua_close.argtypes = [ctypes.c_void_p]

    def run(self, assertions):
        state = self.lib.luaL_newstate()
        if not state:
            raise MemoryError("Lua state could not be created")
        try:
            self.lib.luaL_openlibs(state)
            source = SCRIPT.read_bytes()
            self.lib.lua_pushlstring(state, source, len(source))
            self.lib.lua_setglobal(state, b"FEATURE_SCRIPT")
            program = (HARNESS + "\n" + assertions).encode()
            status = self.lib.luaL_loadbufferx(state, program, len(program), b"feature-tests", b"t")
            if status == 0:
                status = self.lib.lua_pcallk(state, 0, 0, 0, 0, None)
            if status != 0:
                length = ctypes.c_size_t()
                pointer = self.lib.lua_tolstring(state, -1, ctypes.byref(length))
                message = ctypes.string_at(pointer, length.value).decode() if pointer else "Unknown Lua failure"
                raise AssertionError(message)
        finally:
            self.lib.lua_close(state)


@unittest.skipUnless(LUA_LIBRARY, "Lua 5.4/5.3 shared library unavailable; run Testcontainers integration tests")
class FeatureScriptTests(unittest.TestCase):
    def check_lua(self, assertions):
        LuaRuntime().run(assertions)

    def test_duplicate_uses_same_snapshot_without_changing_state(self):
        self.check_lua('''
local first = observe()
local before = writes
assert(observe() == first)
assert(writes == before)
assert(tonumber(db.baseline.value.n) == 1)
assert(cjson.decode(first)[1] == 1)
''')

    def test_changed_payload_is_rejected_before_writes(self):
        self.check_lua('''
observe()
local before = writes
ARGV[2], ARGV[6] = '900', 'different-digest'
assert(observe().err == 'FF_SNAPSHOT_INPUT_CONFLICT')
assert(writes == before)
assert(tonumber(db.baseline.value.n) == 1)
nextTransaction('two', 1000)
assert(values()[1] == 2)
assert(tonumber(db.baseline.value.n) == 2)
''')

    def test_wrong_key_types_never_partially_update_features(self):
        for key, kind in (("velocity", "hash"), ("geo", "string"), ("baseline", "string"), ("snapshot:one", "hash")):
            with self.subTest(key=key):
                self.check_lua(f'''
db['{key}'] = {{kind='{kind}', value={{}}}}
assert(observe().err == 'FF_FEATURE_KEY_TYPE')
assert(writes == 0)
local keys=0; for _ in pairs(db) do keys=keys+1 end; assert(keys == 1)
''')

    def test_legacy_snapshot_cannot_be_recounted(self):
        self.check_lua('''
db[KEYS[4]] = {kind='string', value=cjson.encode({1,1,1,0,0,0,0,0,0,0})}
assert(observe().err == 'FF_SNAPSHOT_FORMAT_CONFLICT')
assert(writes == 0 and db.velocity == nil and db.baseline == nil)
''')

    def test_malformed_snapshot_cannot_be_recounted(self):
        self.check_lua('''
db[KEYS[4]] = {kind='string', value='invalid-json'}
assert(observe().err == 'FF_SNAPSHOT_FORMAT_CONFLICT')
assert(writes == 0 and db.velocity == nil)
''')

    def test_rolling_windows_exclude_the_exact_lower_boundary(self):
        self.check_lua('''
local first=values(); assert(first[1] == 1 and first[2] == 1 and first[3] == 1)
nextTransaction('two', 60000)
local second=values(); assert(second[1] == 1 and second[2] == 2 and second[3] == 2)
nextTransaction('three', 240000)
local third=values(); assert(third[1] == 1 and third[2] == 2 and third[3] == 3)
nextTransaction('four', 3300000)
local fourth=values(); assert(fourth[1] == 1 and fourth[2] == 1 and fourth[3] == 3)
''')

    def test_late_event_does_not_rewind_geolocation(self):
        self.check_lua('''
values()
nextTransaction('two', 1000, 10); values()
nextTransaction('late', 1000, -30); ARGV[3] = tostring(now - 3000)
assert(values()[10] == 1)
assert(tonumber(db.geo.value.lat) == 10)
nextTransaction('newest', 1000, 10)
local newest = values(); assert(newest[4] == 10 and newest[10] == 0)
''')

    def test_baseline_snapshot_excludes_the_current_amount(self):
        self.check_lua('''
local first=values(); assert(first[7] == 0 and first[8] == 0)
nextTransaction('two', 1000); ARGV[2] = '300'
local second=values(); assert(second[7] == 1 and second[8] == 100)
assert(tonumber(db.baseline.value.n) == 2 and tonumber(db.baseline.value.mean) == 200)
assert(tonumber(db.baseline.value.m2) == 20000)
''')


if __name__ == "__main__":
    unittest.main(verbosity=2)
