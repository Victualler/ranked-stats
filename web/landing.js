const STEAM_CDN = "https://cdn.cloudflare.steamstatic.com";
const numberFormatter = new Intl.NumberFormat("en-US");

let profileData = [];
let matchesData = [];
let heroesMap = {};

async function init() {
  try {
    const [heroesRes, profileRes, matchesRes] = await Promise.all([
      fetch("constants/heroes.json"),
      fetch("data/profile.json"),
      fetch("data/matches.json")
    ]);

    heroesMap = await heroesRes.json();
    profileData = await profileRes.json();
    matchesData = await matchesRes.json();

    renderProfile();
    renderMmrChart();
    renderStats();
    setupEventListeners();
  } catch (err) {
    console.error("Failed to load dashboard data:", err);
  }
}

/* ==========================================================================
   1. Profile Info Component
   ========================================================================== */
function renderProfile() {
  if (!Array.isArray(profileData) || profileData.length === 0) return;

  // Use the latest profile snapshot (ordered by unix_time)
  const profile = [...profileData].sort((a, b) => b.unix_time - a.unix_time)[0];

  const rankTier = profile.rank_tier || 0;
  const tier = Math.floor(rankTier / 10);
  const star = rankTier % 10;

  // Rank Badge Images
  const baseImgEl = document.getElementById("rank-base-img");
  const starImgEl = document.getElementById("rank-star-img");

  baseImgEl.src = `assets/rank_icons/rank_icon_${tier}.png`;
  baseImgEl.alt = profile.rank_name || "Rank Icon";

  if (tier > 0 && tier < 8 && star > 0) {
    starImgEl.src = `assets/rank_icons/rank_star_${star}.png`;
    starImgEl.alt = `${star} Star`;
    starImgEl.classList.remove("hidden");
  } else {
    starImgEl.classList.add("hidden");
  }

  // Profile text details
  document.getElementById("rank-name-label").textContent = profile.rank_name || "Uncalibrated";
  document.getElementById("profile-account-id").textContent = profile.account_id ? `ID: ${profile.account_id}` : "Anonymous";
  document.getElementById("profile-badge-points").textContent = profile.badge_points ? numberFormatter.format(profile.badge_points) : "—";
  document.getElementById("profile-lifetime-games").textContent = profile.lifetime_games ? numberFormatter.format(profile.lifetime_games) : "—";
  document.getElementById("profile-tracked-matches").textContent = numberFormatter.format(matchesData.length);

  // Status badge
  const statusEl = document.getElementById("profile-status");
  if (rankTier > 0) {
    statusEl.textContent = "Calibrated";
    statusEl.className = "badge status-badge";
  } else {
    statusEl.textContent = "Calibrating";
    statusEl.className = "badge status-badge uncalibrated";
  }

  // Latest MMR from matches history
  const latestMmrMatch = matchesData.find(m => m.gc && m.gc.start_mmr != null && !m.gc.is_calibration);
  if (latestMmrMatch) {
    const currentMmr = latestMmrMatch.gc.start_mmr + (latestMmrMatch.gc.rank_change || 0);
    document.getElementById("profile-mmr").textContent = numberFormatter.format(currentMmr);
  }
}

/* ==========================================================================
   2. MMR Over All Time Chart Component
   ========================================================================== */
let chartPoints = [];
const CHART_VIEW_W = 1000;
const CHART_VIEW_H = 320;
const PAD_L = 60;
const PAD_R = 25;
const PAD_T = 25;
const PAD_B = 35;

function renderMmrChart() {
  const validMatches = matchesData.filter(m => m.gc && m.gc.start_mmr != null && !m.gc.is_calibration);
  if (validMatches.length === 0) return;

  // Chronological order (oldest to newest)
  const chronological = [...validMatches].reverse();
  chartPoints = chronological.map((m, idx) => {
    const endMmr = m.gc.start_mmr + (m.gc.rank_change || 0);
    return {
      index: idx,
      matchId: m.gc.match_id,
      unixTime: m.gc.unix_time,
      heroId: m.gc.hero_id,
      win: m.gc.win,
      change: m.gc.rank_change,
      mmr: endMmr
    };
  });

  const mmrValues = chartPoints.map(p => p.mmr);
  const minMmr = Math.min(...mmrValues);
  const maxMmr = Math.max(...mmrValues);
  const currentMmr = chartPoints[chartPoints.length - 1].mmr;
  const startMmr = chartPoints[0].mmr;
  const netGain = currentMmr - startMmr;

  // Header Summary Pills
  document.getElementById("chart-total-matches").textContent = numberFormatter.format(chartPoints.length);
  document.getElementById("chart-peak-mmr").textContent = numberFormatter.format(maxMmr);
  document.getElementById("chart-min-mmr").textContent = numberFormatter.format(minMmr);
  
  const netEl = document.getElementById("chart-net-change");
  netEl.textContent = (netGain >= 0 ? `+${numberFormatter.format(netGain)}` : numberFormatter.format(netGain)) + " MMR";
  netEl.className = `pill-value ${netGain >= 0 ? "val-positive" : "val-muted"}`;

  // Date Labels in footer
  document.getElementById("chart-start-date").textContent = formatDate(chartPoints[0].unixTime);
  document.getElementById("chart-end-date").textContent = formatDate(chartPoints[chartPoints.length - 1].unixTime);

  // Axis ranges rounded to nearest 500
  const yMin = Math.max(0, Math.floor((minMmr - 100) / 500) * 500);
  const yMax = Math.ceil((maxMmr + 100) / 500) * 500;

  const plotW = CHART_VIEW_W - PAD_L - PAD_R;
  const plotH = CHART_VIEW_H - PAD_T - PAD_B;

  // Draw Grid Lines & Labels
  const gridLinesGroup = document.getElementById("svg-grid-lines");
  const gridLabelsGroup = document.getElementById("svg-grid-labels");
  gridLinesGroup.innerHTML = "";
  gridLabelsGroup.innerHTML = "";

  const step = 500;
  for (let val = yMin; val <= yMax; val += step) {
    const yRatio = (val - yMin) / (yMax - yMin);
    const yPos = PAD_T + (1 - yRatio) * plotH;

    // Grid Line
    const line = document.createElementNS("http://www.w3.org/2000/svg", "line");
    line.setAttribute("class", "svg-grid-line");
    line.setAttribute("x1", PAD_L);
    line.setAttribute("y1", yPos);
    line.setAttribute("x2", CHART_VIEW_W - PAD_R);
    line.setAttribute("y2", yPos);
    gridLinesGroup.appendChild(line);

    // Label
    const text = document.createElementNS("http://www.w3.org/2000/svg", "text");
    text.setAttribute("class", "svg-grid-text");
    text.setAttribute("x", PAD_L - 8);
    text.setAttribute("y", yPos + 3.5);
    text.textContent = val;
    gridLabelsGroup.appendChild(text);
  }

  // Calculate coordinates for points
  chartPoints.forEach((pt, i) => {
    pt.x = PAD_L + (i / (chartPoints.length - 1)) * plotW;
    pt.y = PAD_T + (1 - (pt.mmr - yMin) / (yMax - yMin)) * plotH;
  });

  // Construct SVG Path strings
  let pathD = `M ${chartPoints[0].x.toFixed(1)} ${chartPoints[0].y.toFixed(1)}`;
  for (let i = 1; i < chartPoints.length; i++) {
    pathD += ` L ${chartPoints[i].x.toFixed(1)} ${chartPoints[i].y.toFixed(1)}`;
  }

  const baselineY = (PAD_T + plotH).toFixed(1);
  const areaD = `${pathD} L ${chartPoints[chartPoints.length - 1].x.toFixed(1)} ${baselineY} L ${chartPoints[0].x.toFixed(1)} ${baselineY} Z`;

  document.getElementById("svg-chart-line").setAttribute("d", pathD);
  document.getElementById("svg-chart-area").setAttribute("d", areaD);
}

/* ==========================================================================
   3. Various Interesting Stats Component
   ========================================================================== */
function renderStats() {
  if (!matchesData || matchesData.length === 0) return;

  const total = matchesData.length;
  const wins = matchesData.filter(m => m.gc && m.gc.win).length;
  const losses = total - wins;
  const winrate = ((wins / total) * 100).toFixed(1);

  document.getElementById("stat-record").textContent = `${wins}W - ${losses}L`;
  document.getElementById("stat-winrate").textContent = `${winrate}%`;
  document.getElementById("stat-winrate-bar").style.width = `${winrate}%`;

  // Streaks Calculation
  const chronological = [...matchesData].reverse();
  let maxStreak = 0;
  let curWinStreak = 0;

  for (const m of chronological) {
    if (m.gc.win) {
      curWinStreak++;
      if (curWinStreak > maxStreak) maxStreak = curWinStreak;
    } else {
      curWinStreak = 0;
    }
  }

  // Active current streak from newest match downwards
  const latestResult = matchesData[0].gc.win;
  let activeStreakCount = 0;
  for (const m of matchesData) {
    if (m.gc.win === latestResult) activeStreakCount++;
    else break;
  }

  document.getElementById("stat-max-streak").textContent = `${maxStreak}W`;
  document.getElementById("stat-current-streak").textContent = `${activeStreakCount}${latestResult ? "W" : "L"}`;
  document.getElementById("stat-current-streak").className = `streak-val ${latestResult ? "val-positive" : "val-muted"}`;
  document.getElementById("stat-streak-badge").textContent = latestResult ? "Winning" : "Reset";

  // Combat & Economy Averages
  const opendotaMatches = matchesData.filter(m => m.opendota && m.opendota.duration != null);
  const count = opendotaMatches.length || 1;

  let totalKills = 0;
  let totalDeaths = 0;
  let totalAssists = 0;
  let totalGpm = 0;
  let totalXpm = 0;
  let totalDuration = 0;

  for (const m of opendotaMatches) {
    totalKills += m.opendota.kills || 0;
    totalDeaths += m.opendota.deaths || 0;
    totalAssists += m.opendota.assists || 0;
    totalGpm += m.opendota.gold_per_min || 0;
    totalXpm += m.opendota.xp_per_min || 0;
    totalDuration += m.opendota.duration || 0;
  }

  const avgK = (totalKills / count).toFixed(1);
  const avgD = (totalDeaths / count).toFixed(1);
  const avgA = (totalAssists / count).toFixed(1);
  const kdaRatio = ((totalKills + totalAssists) / Math.max(1, totalDeaths)).toFixed(2);
  const avgGpm = Math.round(totalGpm / count);
  const avgXpm = Math.round(totalXpm / count);
  const avgDurationMin = Math.round(totalDuration / count / 60);

  document.getElementById("stat-avg-kda").textContent = `${avgK} / ${avgD} / ${avgA}`;
  document.getElementById("stat-kda-ratio").textContent = kdaRatio;
  document.getElementById("stat-avg-gpmxpm").textContent = `${avgGpm} / ${avgXpm}`;
  document.getElementById("stat-avg-duration").textContent = `${avgDurationMin}m`;

  // Top Played Heroes
  renderTopHeroes();
}

function renderTopHeroes() {
  const heroCounts = {};
  for (const m of matchesData) {
    const hid = m.gc.hero_id;
    if (!heroCounts[hid]) {
      heroCounts[hid] = { heroId: hid, games: 0, wins: 0 };
    }
    heroCounts[hid].games++;
    if (m.gc.win) heroCounts[hid].wins++;
  }

  const sortedHeroes = Object.values(heroCounts)
    .sort((a, b) => b.games - a.games)
    .slice(0, 5);

  const container = document.getElementById("top-heroes-grid");
  container.innerHTML = "";

  for (const h of sortedHeroes) {
    const hero = heroesMap[h.heroId] || { localized_name: `Hero ${h.heroId}`, img: "" };
    const heroImgUrl = hero.img ? `${STEAM_CDN}${hero.img}` : "";
    const wr = ((h.wins / h.games) * 100).toFixed(1);
    const isPositive = Number(wr) >= 50;

    const card = document.createElement("div");
    card.className = "hero-stat-card";
    card.innerHTML = `
      <img class="hero-card-img" src="${heroImgUrl}" alt="${hero.localized_name}" loading="lazy" decoding="async">
      <div class="hero-card-info">
        <span class="hero-card-name">${hero.localized_name}</span>
        <div class="hero-card-metrics">
          <span>${h.games} games</span>
          <span>&bull;</span>
          <span class="hero-card-winrate ${isPositive ? "positive" : "negative"}">${wr}% WR</span>
        </div>
      </div>
    `;
    container.appendChild(card);
  }
}

/* ==========================================================================
   Chart Interactions & Tooltips
   ========================================================================== */
function setupEventListeners() {
  const chartWrapper = document.getElementById("chart-container");
  const crosshair = document.getElementById("svg-crosshair");
  const activeDot = document.getElementById("svg-active-dot");
  const tooltip = document.getElementById("chart-tooltip");

  chartWrapper.addEventListener("mousemove", (e) => {
    if (!chartPoints || chartPoints.length === 0) return;

    const rect = chartWrapper.getBoundingClientRect();
    const mouseX = e.clientX - rect.left;
    const svgX = (mouseX / rect.width) * CHART_VIEW_W;

    // Find nearest point
    let nearest = chartPoints[0];
    let minDiff = Math.abs(chartPoints[0].x - svgX);

    for (let i = 1; i < chartPoints.length; i++) {
      const diff = Math.abs(chartPoints[i].x - svgX);
      if (diff < minDiff) {
        minDiff = diff;
        nearest = chartPoints[i];
      }
    }

    // Update crosshair & dot
    crosshair.setAttribute("x1", nearest.x);
    crosshair.setAttribute("x2", nearest.x);
    crosshair.classList.remove("hidden");

    activeDot.setAttribute("cx", nearest.x);
    activeDot.setAttribute("cy", nearest.y);
    activeDot.classList.remove("hidden");

    // Tooltip content
    const hero = heroesMap[nearest.heroId] || { localized_name: `Hero ${nearest.heroId}`, img: "" };
    const heroImg = hero.img ? `${STEAM_CDN}${hero.img}` : "";
    const changeStr = nearest.change > 0 ? `+${nearest.change}` : `${nearest.change}`;
    const changeClass = nearest.change > 0 ? "val-positive" : "val-muted";

    tooltip.innerHTML = `
      <div class="chart-tip-row">
        ${heroImg ? `<img class="chart-tip-hero-img" src="${heroImg}" alt="${hero.localized_name}">` : ""}
        <div>
          <div><strong class="chart-tip-mmr">${numberFormatter.format(nearest.mmr)} MMR</strong> <span class="${changeClass}">(${changeStr})</span></div>
          <div class="chart-tip-date">${formatDate(nearest.unixTime)} &bull; ${hero.localized_name}</div>
        </div>
      </div>
    `;

    tooltip.style.left = `${e.clientX + 14}px`;
    tooltip.style.top = `${e.clientY - 20}px`;
    tooltip.classList.remove("hidden");
  });

  chartWrapper.addEventListener("mouseleave", () => {
    crosshair.classList.add("hidden");
    activeDot.classList.add("hidden");
    tooltip.classList.add("hidden");
  });
}

function formatDate(unixTime) {
  const d = new Date(unixTime * 1000);
  const day = String(d.getDate()).padStart(2, "0");
  const month = String(d.getMonth() + 1).padStart(2, "0");
  const year = d.getFullYear();
  return `${day}/${month}/${year}`;
}

document.addEventListener("DOMContentLoaded", init);

