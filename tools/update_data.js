const fs = require('fs');

// ==========================================
// CONFIG: GAME/APP BLACKLIST
// ==========================================
const IGNORED_NAMES = new Set([
  "dolphin",
  "project64",
  "project 64",
  "pcsx2",
  "retroarch",
  "visualboyadvance",
  "steam"
]);

async function runProcess() {
  try {
    console.log("Starting download of the Discord file...");
    const response = await fetch('https://discord.com/api/v10/applications/detectable');
    if (!response.ok) throw new Error(`Failed to download from Discord: ${response.statusText}`);

    const rawDiscordGames = await response.json();

    // Filter the Discord list
    const discordGames = rawDiscordGames.filter(game => {
      if (!game.name) return false;
      const normalizedName = game.name.trim().toLowerCase();
      return !IGNORED_NAMES.has(normalizedName);
    });

    // 1. Read the local detectable.json file
    let localDetectable = [];
    if (fs.existsSync('detectable.json')) {
      localDetectable = JSON.parse(fs.readFileSync('detectable.json', 'utf8'));
    }

    const localMap = new Map(localDetectable.map(game => [game.id, game]));
    const discordMap = new Map(discordGames.map(game => [game.id, game]));

    const newGames = [];
    const modifiedGames = [];

    // 2. Find and classify games based ONLY on the essential fields
    for (const [id, discordGame] of discordMap) {
      const localGame = localMap.get(id);

      if (!localGame) {
        console.log(`[NEW] -> (${discordGame.name})`);
        newGames.push(discordGame);
      } else {
        const localEssential = pickEssentialFields(localGame);
        const discordEssential = pickEssentialFields(discordGame);

        const localSerialized = JSON.stringify(canonicalize(localEssential));
        const discordSerialized = JSON.stringify(canonicalize(discordEssential));

        if (localSerialized !== discordSerialized) {
          console.log(`[CHANGED] -> (${discordGame.name})`);
          modifiedGames.push(discordGame);
        }
      }
    }

    // 3. Stop early if there are no changes
    if (newGames.length === 0 && modifiedGames.length === 0) {
      console.log("No changes detected in the essential fields of any game. Process finished.");
      return;
    }

    console.log(`Summary: ${newGames.length} new and ${modifiedGames.length} modified.`);

    // 4. Read the local detectable_processed.json file
    let localProcessed = [];
    if (fs.existsSync('detectable_processed.json')) {
      localProcessed = JSON.parse(fs.readFileSync('detectable_processed.json', 'utf8'));
    }
    const processedMap = new Map(localProcessed.map(game => [game.id, game]));

    // HANDLE MODIFIED GAMES (keeping the previous id_igdb, cover, artwork and URL without hitting the API)
    for (const game of modifiedGames) {
      const previousGame = processedMap.get(game.id);
      const existingIgdbId = previousGame ? previousGame.id_igdb : null;
      const existingCover = previousGame ? previousGame.cover : null;
      const existingArtwork = previousGame ? previousGame.artwork : null;
      const existingUrl = previousGame ? previousGame.url : null; // <-- NEW

      const updatedGame = buildGameEntry(game, existingIgdbId, existingCover, existingArtwork, existingUrl);
      processedMap.set(game.id, updatedGame);
    }

    // HANDLE NEW GAMES (classify them for the API lookups)
    const gamesWithSteam = [];
    const gamesWithoutSteam = [];

    for (const game of newGames) {
      const steamSku = game.third_party_skus?.find(sku => sku.distributor === 'steam' && sku.id);
      if (steamSku) {
        gamesWithSteam.push({ game, steamId: steamSku.id });
      } else {
        gamesWithoutSteam.push(game);
      }
    }

    const sleep = ms => new Promise(res => setTimeout(res, ms));

    // Steam batches (NEW games only)
    const batchSize = 100;
    for (let i = 0; i < gamesWithSteam.length; i += batchSize) {
      const currentBatch = gamesWithSteam.slice(i, i + batchSize);
      const steamIdList = currentBatch.map(item => item.steamId);

      console.log(`Querying the API for a batch of ${steamIdList.length} new Steam games...`);
      const igdbIdsBySteamId = await fetchIgdbIdsBySteamBatch(steamIdList);

      for (const item of currentBatch) {
        const igdbId = igdbIdsBySteamId[item.steamId] || null;
        // Media fields and url start out as null
        processedMap.set(item.game.id, buildGameEntry(item.game, igdbId, null, null, null));
      }
      await sleep(300);
    }

    // Individual lookups by name (only for NEW games without Steam)
    for (const game of gamesWithoutSteam) {
      console.log(`Querying the API by name for new game: ${game.name}`);
      const igdbId = await fetchIgdbIdByName(game.name);
      // Media fields and url start out as null
      processedMap.set(game.id, buildGameEntry(game, igdbId, null, null, null));
      await sleep(300);
    }

    // 6. Enrich the NEW games that got an id_igdb with cover, artwork and URL
    const newIgdbIds = [];
    const discordIdByIgdbId = {};

    for (const game of [...newGames]) {
      const processed = processedMap.get(game.id);
      if (processed && processed.id_igdb) {
        newIgdbIds.push(processed.id_igdb);
        discordIdByIgdbId[processed.id_igdb] = game.id;
      }
    }

    if (newIgdbIds.length > 0) {
      console.log(`Fetching additional metadata for ${newIgdbIds.length} new games...`);

      // 6.1 Covers
      const coversBatchSize = 500;
      const allCovers = {};
      for (let i = 0; i < newIgdbIds.length; i += coversBatchSize) {
        const batchIds = newIgdbIds.slice(i, i + coversBatchSize);
        console.log(`Querying covers for a batch of ${batchIds.length} games...`);
        const coverMap = await fetchCoversBatch(batchIds);
        Object.assign(allCovers, coverMap);
        await sleep(300);
      }

      // 6.2 Artworks
      const artworksBatchSize = 50;
      const allArtworks = {};
      for (let i = 0; i < newIgdbIds.length; i += artworksBatchSize) {
        const batchIds = newIgdbIds.slice(i, i + artworksBatchSize);
        console.log(`Querying artworks for a batch of ${batchIds.length} games...`);
        const artworkMap = await fetchArtworksBatch(batchIds);
        Object.assign(allArtworks, artworkMap);
        await sleep(300);
      }

      // 6.3 URLs (NEW) - Up to 500 per batch, as supported by the worker
      const urlsBatchSize = 500;
      const allUrls = {};
      for (let i = 0; i < newIgdbIds.length; i += urlsBatchSize) {
        const batchIds = newIgdbIds.slice(i, i + urlsBatchSize);
        console.log(`Querying URLs for a batch of ${batchIds.length} games...`);
        const urlMap = await fetchUrlsBatch(batchIds);
        Object.assign(allUrls, urlMap);
        await sleep(300);
      }

      // Assign the final metadata to each game
      for (const igdbId of newIgdbIds) {
        const discordId = discordIdByIgdbId[igdbId];
        const processed = processedMap.get(discordId);
        if (processed) {
          const coverUrl = allCovers[igdbId] || null;
          processed.cover = coverUrl ? extractImageId(coverUrl) : null;

          const artworkUrls = allArtworks[igdbId] || null;
          processed.artwork = artworkUrls ? artworkUrls.map(extractImageId) : null;

          // Assign the URL slug
          const fullUrl = allUrls[igdbId] || null;
          processed.url = fullUrl ? extractUrlSlug(fullUrl) : null;

          processedMap.set(discordId, processed);
        }
      }
    }

    // 7. Write the files back to disk
    fs.writeFileSync('detectable.json', JSON.stringify(discordGames, null, 2), 'utf8');
    const processedArray = Array.from(processedMap.values());
    fs.writeFileSync('detectable_processed.json', JSON.stringify(processedArray, null, 2), 'utf8');

    console.log("JSON files successfully updated and saved.");

  } catch (error) {
    console.error("Critical error during the process:", error);
    process.exit(1);
  }
}

// ==========================================
// HELPER FUNCTIONS
// ==========================================

function pickEssentialFields(game) {
  if (!game) return null;
  return {
    id: game.id,
    name: game.name,
    aliases: game.aliases || [],
    executables: (game.executables || []).map(e => ({
      name: e.name,
      os: e.os,
      is_launcher: e.is_launcher ?? false
    })),
    third_party_skus: (game.third_party_skus || []).map(s => ({
      distributor: s.distributor,
      id: s.id
    }))
  };
}

// Added the "igdbUrl" parameter
function buildGameEntry(game, igdbId, cover, artwork, igdbUrl) {
  return {
    id: game.id,
    name: game.name,
    aliases: game.aliases || [],
    executables: (game.executables || []).map(e => ({
      name: e.name,
      os: e.os,
      is_launcher: e.is_launcher ?? false
    })),
    third_party_skus: (game.third_party_skus || []).map(s => ({
      distributor: s.distributor,
      id: s.id
    })),
    id_igdb: igdbId,
    cover: cover ?? null,
    artwork: artwork ?? null,
    url: igdbUrl ?? null // <-- Added key
  };
}

// Extracts the image_id from an IGDB URL
function extractImageId(url) {
  if (!url) return null;
  const parts = url.split('/');
  const fileName = parts[parts.length - 1];
  return fileName.replace(/\.[^.]+$/, '');
}

// Extracts the slug from an IGDB URL (e.g. "https://www.igdb.com/games/cyberpunk-2077" → "cyberpunk-2077")
function extractUrlSlug(url) {
  if (!url) return null;
  const parts = url.split('/');
  return parts[parts.length - 1] || null;
}

function canonicalize(obj) {
  if (obj === null || typeof obj !== 'object') return obj;

  if (Array.isArray(obj)) {
    const normalizedItems = obj.map(canonicalize);
    return normalizedItems.sort((a, b) => {
      const strA = typeof a === 'object' ? JSON.stringify(a) : String(a);
      const strB = typeof b === 'object' ? JSON.stringify(b) : String(b);
      if (strA < strB) return -1;
      if (strA > strB) return 1;
      return 0;
    });
  }

  const result = {};
  Object.keys(obj).sort().forEach(key => {
    result[key] = canonicalize(obj[key]);
  });
  return result;
}

// ==========================================
// CALLS TO THE CLOUDFLARE BACKEND
// ==========================================

async function fetchIgdbIdsBySteamBatch(steamIds) {
  try {
    const url = `https://apploggd.nik250dev.workers.dev/api/v1/igdb/steam/batch`;
    const response = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(steamIds)
    });
    if (!response.ok) throw new Error(`Batch error: ${response.statusText}`);
    return await response.json();
  } catch (err) {
    console.error("Error in the Steam batch request:", err);
    return {};
  }
}

async function fetchIgdbIdByName(gameName) {
  try {
    const url = `https://apploggd.nik250dev.workers.dev/api/v1/igdb/search?query=${encodeURIComponent(gameName)}`;
    const response = await fetch(url);
    if (!response.ok) throw new Error(`Individual request error: ${response.statusText}`);
    const data = await response.json();
    return data.id_igdb;
  } catch (err) {
    console.error(`Error while querying the game "${gameName}":`, err);
    return null;
  }
}

async function fetchCoversBatch(gameIds) {
  try {
    const url = `https://apploggd.nik250dev.workers.dev/api/v1/igdb/covers`;
    const response = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(gameIds)
    });
    if (!response.ok) throw new Error(`Covers batch error: ${response.statusText}`);
    return await response.json();
  } catch (err) {
    console.error("Error in the covers batch request:", err);
    return {};
  }
}

async function fetchArtworksBatch(gameIds) {
  try {
    const url = `https://apploggd.nik250dev.workers.dev/api/v1/igdb/artworks`;
    const response = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(gameIds)
    });
    if (!response.ok) throw new Error(`Artworks batch error: ${response.statusText}`);
    return await response.json();
  } catch (err) {
    console.error("Error in the artworks batch request:", err);
    return {};
  }
}

// NEW CALL FOR THE URLs
async function fetchUrlsBatch(gameIds) {
  try {
    const url = `https://apploggd.nik250dev.workers.dev/api/v1/igdb/url/batch`;
    const response = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(gameIds)
    });
    if (!response.ok) throw new Error(`URLs batch error: ${response.statusText}`);
    return await response.json();
  } catch (err) {
    console.error("Error in the URLs batch request:", err);
    return {};
  }
}

runProcess();
