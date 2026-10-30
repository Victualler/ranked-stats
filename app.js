const STEAM_CDN = "https://cdn.cloudflare.steamstatic.com";

let heroesMap = {};

async function init() {
  try {
    const [heroesRes, matchesRes] = await Promise.all([
      fetch("constants/heroes.json"),
      fetch("data/matches.json")
    ]);

    heroesMap = await heroesRes.json();
    const matches = await matchesRes.json();

    renderTable(matches);
  } catch (err) {
    console.error("Failed to load match tracking records:", err);
  }
}

function renderTable(matches) {
  const tbody = document.getElementById("matches-body");
  const fragment = document.createDocumentFragment();

  for (const m of matches) {
    const tr = document.createElement("tr");
    tr.className = m.gc.win ? "win" : "loss";

    // Date & 24h Time (Browser Region Local)
    const dateObj = new Date(m.gc.unix_time * 1000);
    const dateStr = dateObj.toLocaleDateString("en-CA"); // YYYY-MM-DD
    const timeStr = dateObj.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false });

    // Hero metadata
    const hero = heroesMap[m.gc.hero_id] || { localized_name: "Unknown", img: "", icon: "" };
    const heroImgUrl = hero.img ? `${STEAM_CDN}${hero.img}` : "";

    // End MMR calculation
    let mmrDisplay = "";
    if (m.gc.is_calibration || m.gc.start_mmr === null) {
      mmrDisplay = `<span class="calibration-text">Calibration</span>`;
    } else {
      const endMmr = m.gc.start_mmr + (m.gc.rank_change || 0);
      mmrDisplay = endMmr.toString();
    }

    // MMR Change string
    let changeDisplay = "";
    if (m.gc.rank_change !== null && m.gc.rank_change !== undefined) {
      if (m.gc.rank_change > 0) {
        changeDisplay = `<span class="val-positive">+${m.gc.rank_change}</span>`;
      } else if (m.gc.rank_change < 0) {
        changeDisplay = `<span class="val-negative">${m.gc.rank_change}</span>`;
      } else {
        changeDisplay = "0";
      }
    }

    // Duration formatting
    const durationStr = formatDuration(m.opendota?.duration);

    // 2x5 Draft Generation
    const draftHtml = buildDraftGrid(m.opendota?.players, m.gc.hero_id);

    tr.innerHTML = `
      <td>${dateStr}</td>
      <td>${timeStr}</td>
      <td>
        <img class="hero-portrait" src="${heroImgUrl}" alt="${hero.localized_name}" 
             data-tooltip="${hero.localized_name}" loading="lazy" decoding="async">
      </td>
      <td>${mmrDisplay}</td>
      <td>${changeDisplay}</td>
      <td>${durationStr}</td>
      <td>${draftHtml}</td>
      <td>${m.opendota?.level ?? "—"}</td>
      <td>${m.opendota?.kills ?? "—"}</td>
      <td>${m.opendota?.deaths ?? "—"}</td>
      <td>${m.opendota?.assists ?? "—"}</td>
      <td>${m.opendota?.last_hits ?? "—"}</td>
      <td>${m.opendota?.denies ?? "—"}</td>
      <td>${m.opendota?.net_worth ? m.opendota.net_worth.toLocaleString() : "—"}</td>
      <td>${m.opendota?.gold_per_min ?? "—"}</td>
      <td>${m.opendota?.xp_per_min ?? "—"}</td>
      <td>
        <div class="match-cell">
          <span class="copy-id" data-mid="${m.gc.match_id}" title="Click to copy">${m.gc.match_id}</span>
          <a class="external-link" href="https://www.opendota.com/matches/${m.gc.match_id}" target="_blank" rel="noopener">↗</a>
        </div>
      </td>
    `;

    fragment.appendChild(tr);
  }

  tbody.appendChild(fragment);
  setupEventDelegation();
}

function formatDuration(seconds) {
  if (!seconds) return "—";
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = seconds % 60;
  const mStr = m.toString().padStart(2, "0");
  const sStr = s.toString().padStart(2, "0");
  return h > 0 ? `${h}:${mStr}:${sStr}` : `${m}:${sStr}`;
}

function buildDraftGrid(players, myHeroId) {
  if (!players || players.length === 0) return "—";

  // Split into Dire (top row) and Radiant (bottom row)
  const dire = players.filter(p => !p.isRadiant);
  const radiant = players.filter(p => p.isRadiant);
  const ordered = [...dire, ...radiant];

  let html = `<div class="draft-grid">`;
  for (const p of ordered) {
    const hero = heroesMap[p.hero_id] || { localized_name: `Hero ${p.hero_id}`, icon: "" };
    const iconUrl = hero.icon ? `${STEAM_CDN}${hero.icon}` : "";
    const isSelf = p.hero_id === myHeroId;
    const playerAcc = p.account_id ? p.account_id : "N/A";
    const tooltipText = `${hero.localized_name} (${playerAcc})`;

    html += `
      <img class="draft-icon ${isSelf ? 'self' : ''}" 
           src="${iconUrl}" 
           data-tooltip="${tooltipText}"
           loading="lazy" 
           decoding="async" 
           alt="${hero.localized_name}">
    `;
  }
  html += `</div>`;
  return html;
}

function setupEventDelegation() {
  const table = document.getElementById("matches-table");
  const tooltip = document.getElementById("tooltip");

  // Copy Match ID Event Delegation
  table.addEventListener("click", (e) => {
    const copyTarget = e.target.closest(".copy-id");
    if (copyTarget) {
      const mid = copyTarget.dataset.mid;
      navigator.clipboard.writeText(mid).then(() => {
        const originalText = copyTarget.textContent;
        copyTarget.textContent = "Copied!";
        setTimeout(() => copyTarget.textContent = originalText, 1200);
      });
    }
  });

  // Tooltip Mouseover Delegation
  table.addEventListener("mouseover", (e) => {
    const target = e.target.closest("[data-tooltip]");
    if (target) {
      tooltip.textContent = target.dataset.tooltip;
      tooltip.classList.remove("hidden");
    }
  });

  table.addEventListener("mousemove", (e) => {
    if (!tooltip.classList.contains("hidden")) {
      tooltip.style.left = `${e.clientX + 12}px`;
      tooltip.style.top = `${e.clientY + 12}px`;
    }
  });

  table.addEventListener("mouseout", (e) => {
    const target = e.target.closest("[data-tooltip]");
    if (target) {
      tooltip.classList.add("hidden");
    }
  });
}

document.addEventListener("DOMContentLoaded", init);
