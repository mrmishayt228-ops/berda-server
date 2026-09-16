// ============================================================
//  Berda auth + key server for Cloudflare Workers + D1.
//  Same JSON contract as the .NET server (camelCase fields):
//    POST /api/register          {login,password}
//    POST /api/login             {login,password}
//    POST /api/logout            {token}
//    GET  /api/profile           ?token=
//    POST /api/premium/activate  {token,keyCode}
//    POST /api/keys/create       {token,days,hours,note}
//    GET  /api/keys/list         ?token=
//    POST /api/keys/delete       {token,keyCode}
//  Deploy: paste this file into a Cloudflare Worker, bind a
//  fresh D1 database as `DB` (binding name), optionally set
//  OWNER_LOGIN / OWNER_PASSWORD variables.
// ============================================================

const KEY_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

function json(status, obj) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { "content-type": "application/json; charset=utf-8" },
  });
}

async function readBody(request) {
  try {
    return await request.json();
  } catch {
    return null;
  }
}

function nowIso() {
  return new Date().toISOString();
}

function hex(bytes) {
  return [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function tokenHex() {
  const b = new Uint8Array(24);
  crypto.getRandomValues(b);
  return hex(b);
}

function keyCode() {
  const b = new Uint8Array(16);
  crypto.getRandomValues(b);
  let s = "";
  for (let i = 0; i < 16; i++) {
    if (i > 0 && i % 4 === 0) s += "-";
    s += KEY_ALPHABET[b[i] % KEY_ALPHABET.length];
  }
  return s;
}

function keyIsValid(code) {
  if (!code) return false;
  for (const c of code) {
    if (c === "-") continue;
    if (KEY_ALPHABET.indexOf(c) < 0) return false;
  }
  return true;
}

async function hashPassword(password) {
  const salt = new Uint8Array(16);
  crypto.getRandomValues(salt);
  const saltHex = hex(salt);
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(saltHex + ":" + password));
  return saltHex + ":" + hex(new Uint8Array(digest));
}

async function verifyPassword(password, stored) {
  const i = stored.indexOf(":");
  if (i <= 0) return false;
  const saltHex = stored.slice(0, i);
  const expectHex = stored.slice(i + 1);
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(saltHex + ":" + password));
  return hex(new Uint8Array(digest)) === expectHex;
}

async function getLoginForToken(env, token) {
  if (!token) return null;
  const row = await env.DB.prepare("SELECT login FROM sessions WHERE token = ?").bind(token).first();
  return row ? row.login : null;
}

async function getAccount(env, login) {
  if (!login) return null;
  return await env.DB.prepare(
    "SELECT login, password_hash, role, premium_until FROM accounts WHERE login = ?"
  ).bind(login).first();
}

function premiumUntil(account, now) {
  if (account && account.premium_until && account.premium_until > now) return account.premium_until;
  return null;
}

// ---------- endpoints ----------

async function register(request, env) {
  const dto = await readBody(request);
  if (!dto) return json(400, { ok: false, error: "bad body" });
  const login = String(dto.login || "").trim();
  const password = String(dto.password || "");
  if (login.length < 3 || login.length > 24 || !/^[A-Za-z0-9_]+$/.test(login)) {
    return json(400, { ok: false, error: "login: 3-24 chars, letters/digits/_" });
  }
  if (password.length < 4) {
    return json(400, { ok: false, error: "password: at least 4 characters" });
  }
  let token;
  try {
    await env.DB.prepare("INSERT INTO accounts(login, password_hash) VALUES(?, ?)")
      .bind(login, await hashPassword(password)).run();
    token = tokenHex();
    await env.DB.prepare("INSERT INTO sessions(token, login) VALUES(?, ?)").bind(token, login).run();
  } catch (e) {
    if (String(e.message).includes("UNIQUE") || String(e.message).includes("constraint")) {
      return json(409, { ok: false, error: "login already taken" });
    }
    throw e;
  }
  return json(200, { ok: true, error: null, token, login, role: "user", isPremium: false, premiumUntil: null });
}

async function login(request, env) {
  const dto = await readBody(request);
  if (!dto) return json(400, { ok: false, error: "bad body" });
  const login = String(dto.login || "").trim();
  const acc = await getAccount(env, login);
  if (!acc || !(await verifyPassword(String(dto.password || ""), acc.password_hash))) {
    return json(401, { ok: false, error: "invalid login or password" });
  }
  const token = tokenHex();
  await env.DB.prepare("INSERT INTO sessions(token, login) VALUES(?, ?)").bind(token, login).run();
  const now = nowIso();
  const premium = premiumUntil(acc, now);
  return json(200, { ok: true, error: null, token, login: acc.login, role: acc.role, isPremium: premium !== null, premiumUntil: premium });
}

async function logout(request, env) {
  const dto = await readBody(request);
  if (dto && dto.token) {
    await env.DB.prepare("DELETE FROM sessions WHERE token = ?").bind(dto.token).run();
  }
  return json(200, { ok: true, error: null });
}

async function profile(request, env) {
  const url = new URL(request.url);
  const login = await getLoginForToken(env, url.searchParams.get("token"));
  if (!login) {
    return json(401, { ok: false, error: "not authorized" });
  }
  const acc = await getAccount(env, login);
  const now = nowIso();
  const premium = premiumUntil(acc, now);
  return json(200, { ok: true, error: null, login, role: acc ? acc.role : "user", isPremium: premium !== null, premiumUntil: premium });
}

async function activate(request, env) {
  const dto = await readBody(request);
  if (!dto) return json(400, { ok: false, error: "bad body" });
  const login = await getLoginForToken(env, dto.token);
  if (!login) {
    return json(401, { ok: false, error: "not authorized" });
  }
  const code = String(dto.keyCode || "").trim().toUpperCase();
  if (!keyIsValid(code)) {
    return json(400, { ok: false, error: "invalid key format" });
  }
  const acc = await getAccount(env, login);
  const claimed = await env.DB.prepare(
    "UPDATE keys SET used = 1, used_by = ?, used_at = ? WHERE code = ? AND used = 0"
  ).bind(login, nowIso(), code).run();
  if (!claimed.meta || claimed.meta.changes !== 1) {
    return json(409, { ok: false, error: "key not found or already used" });
  }
  const k = await env.DB.prepare("SELECT total_hours FROM keys WHERE code = ?").bind(code).first();
  if (!k) {
    return json(409, { ok: false, error: "key not found or already used" });
  }
  const now = nowIso();
  const base = premiumUntil(acc, now) || now;
  const until = new Date(Date.parse(base) + k.total_hours * 3600_000).toISOString();
  await env.DB.prepare("UPDATE accounts SET premium_until = ? WHERE login = ?").bind(until, login).run();
  return json(200, { ok: true, error: null, premiumUntil: until });
}

function clamp(n, lo, hi) {
  return Math.max(lo, Math.min(hi, n));
}

async function createKey(request, env) {
  const dto = await readBody(request);
  if (!dto) return json(400, { ok: false, error: "bad body" });
  const login = await getLoginForToken(env, dto.token);
  const acc = login ? await getAccount(env, login) : null;
  if (!acc || acc.role !== "owner") {
    return json(403, { ok: false, error: "owner only" });
  }
  const days = clamp(Math.trunc(Number(dto.days) || 0), 0, 3650);
  const hours = clamp(Math.trunc(Number(dto.hours) || 0), 0, 23);
  const total = days * 24 + hours;
  if (total <= 0) {
    return json(400, { ok: false, error: "duration must be at least 1 hour" });
  }
  const code = keyCode();
  const note = dto.note != null && String(dto.note).trim() !== "" ? String(dto.note).trim() : null;
  await env.DB.prepare("INSERT INTO keys(code, total_hours, note) VALUES(?, ?, ?)").bind(code, total, note).run();
  return json(200, { ok: true, error: null, keyCode: code, totalHours: total });
}

async function listKeys(request, env) {
  const url = new URL(request.url);
  const login = await getLoginForToken(env, url.searchParams.get("token"));
  const acc = login ? await getAccount(env, login) : null;
  if (!acc || acc.role !== "owner") {
    return json(403, { ok: false, error: "owner only" });
  }
  const rows = await env.DB.prepare("SELECT * FROM keys ORDER BY created_at DESC").all();
  const keys = rows.results.map((k) => ({
    code: k.code,
    totalHours: k.total_hours,
    note: k.note ?? null,
    createdAt: k.created_at,
    used: k.used === 1,
    usedBy: k.used_by ?? null,
    usedAt: k.used_at ?? null,
  }));
  return json(200, { ok: true, error: null, keys });
}

async function deleteKey(request, env) {
  const dto = await readBody(request);
  if (!dto) return json(400, { ok: false, error: "bad body" });
  const login = await getLoginForToken(env, dto.token);
  const acc = login ? await getAccount(env, login) : null;
  if (!acc || acc.role !== "owner") {
    return json(403, { ok: false, error: "owner only" });
  }
  const code = String(dto.keyCode || "").trim().toUpperCase();
  const res = await env.DB.prepare("DELETE FROM keys WHERE code = ?").bind(code).run();
  if (!res.meta || res.meta.changes !== 1) {
    return json(404, { ok: false, error: "key not found" });
  }
  return json(200, { ok: true, error: null });
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const path = url.pathname;
    const method = request.method;

    // Lazily make sure the owner account exists (env over plain defaults).
    const ownerLogin = env.OWNER_LOGIN || "owner";
    const ownerPassword = env.OWNER_PASSWORD || "EstiAdmin2026";
    await env.DB.prepare(
      "INSERT OR IGNORE INTO accounts(login, password_hash, role) VALUES(?, ?, 'owner')"
    ).bind(ownerLogin, await hashPassword(ownerPassword)).run();

    try {
      if (method === "POST" && path === "/api/register") return await register(request, env);
      if (method === "POST" && path === "/api/login") return await login(request, env);
      if (method === "POST" && path === "/api/logout") return await logout(request, env);
      if (method === "POST" && path === "/api/premium/activate") return await activate(request, env);
      if (method === "GET" && path === "/api/profile") return await profile(request, env);
      if (method === "POST" && path === "/api/keys/create") return await createKey(request, env);
      if (method === "GET" && path === "/api/keys/list") return await listKeys(request, env);
      if (method === "POST" && path === "/api/keys/delete") return await deleteKey(request, env);
      return json(404, { ok: false, error: "unknown route" });
    } catch (e) {
      console.error("worker error:", e);
      return json(500, { ok: false, error: "internal error" });
    }
  },
};