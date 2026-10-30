const STEAM_CDN = "https://cdn.cloudflare.steamstatic.com";
const PAGE_SIZE = 20;

let allMatches = [];
let heroesMap = {};
let currentPage = 1;

async function init() {
  try {
    const [heroesRes, matchesRes] = await Promise.all([
      fetch("constants/heroes.json"),
      fetch("data/matches.json")
    ]);

    heroesMap = await heroesRes.json();
    allMatches = await matchesRes.json();

    document.getElementById("match-count").textContent = `${allMatches.length} matches`;

    renderTable();
    setupEventListeners();
  } catch (err) {
    console.error("Initialization failed:", err);
  }
}

function renderTable() {
  const tbody = document.getElementById("matches-body");
  tbody.innerHTML = "";

  const totalMatches = allMatches.length;
  const totalPages = Math.ceil(totalMatches / PAGE_SIZE) || 1;
  
  if (currentPage > totalPages) currentPage = totalPages;
  if (currentPage < 1) currentPage = 1;

  const start = (currentPage - 1) * PAGE_SIZE;
  const pageMatches = allMatches.slice(start, start + PAGE_SIZE);

  const fragment = document.createDocumentFragment();

  for (const m of pageMatches) {
    const tr = document.createElement("tr");
    tr.className = m.gc.win ? "win" : "loss";

    // Date & Time (User browser locale/zone)
    const dateObj = new Date(m.gc.unix_time * 1000);
    const dateStr = dateObj.toLocaleDateString("en-CA"); // YYYY-MM-DD
    const timeStr = dateObj.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hour12: false });

    // Hero Topbar Portrait
    const hero = heroesMap[m.gc.hero_id] || { localized_name: "Unknown", img: "", icon: "" };
    const heroImgUrl = hero.img ? `${STEAM_CDN}${hero.img}` : "";

    // End MMR & Change
    let mmrDisplay = "";
    let changeDisplay = "";

    if (m.gc.is_calibration || m.gc.start_mmr === null) {
      mmrDisplay = `<span class="calibration-text">Calibration</span>`;
      changeDisplay = "";
    } else {
      const endMmr = m.gc.start_mmr + (m.gc.rank_change || 0);
      mmrDisplay = endMmr.toString();

      if (m.gc.rank_change > 0) {
        changeDisplay = `<span class="val-positive">+${m.gc.rank_change}</span>`;
      } else if (m.gc.rank_change < 0) {
        changeDisplay = `<span class="val-negative">${m.gc.rank_change}</span>`;
      } else {
        changeDisplay = `<span style="color:var(--text-muted)">0</span>`;
      }
    }

    const durationStr = formatDuration(m.opendota?.duration);
    const draftHtml = buildDraftGrid(m.opendota?.players, m.gc.hero_id);

    tr.innerHTML = `
      <td class="col-date">${dateStr}</td>
      <td class="col-time group-end">${timeStr}</td>
      <td class="col-hero group-end">
        <img class="hero-portrait" src="${heroImgUrl}" alt="${hero.localized_name}" 
             data-tooltip="${hero.localized_name}" loading="lazy" decoding="async">
      </td>
      <td class="col-mmr">${mmrDisplay}</td>
      <td class="col-change group-end">${changeDisplay}</td>
      <td class="col-duration group-end">${durationStr}</td>
      <td class="col-draft group-end">${draftHtml}</td>
      <td class="col-lvl group-end">${m.opendota?.level ?? "—"}</td>
      <td class="col-k">${m.opendota?.kills ?? "—"}</td>
      <td class="col-d">${m.opendota?.deaths ?? "—"}</td>
      <td class="col-a group-end">${m.opendota?.assists ?? "—"}</td>
      <td class="col-lh">${m.opendota?.last_hits ?? "—"}</td>
      <td class="col-dn group-end">${m.opendota?.denies ?? "—"}</td>
      <td class="col-net group-end">${m.opendota?.net_worth ? m.opendota.net_worth.toLocaleString() : "—"}</td>
      <td class="col-gpm">${m.opendota?.gold_per_min ?? "—"}</td>
      <td class="col-xpm group-end">${m.opendota?.xp_per_min ?? "—"}</td>
      <td class="col-match">
        <div class="match-cell">
          <span class="copy-id" data-mid="${m.gc.match_id}" title="Click to copy">${m.gc.match_id}</span>
          <a class="external-link" href="https://www.opendota.com/matches/${m.gc.match_id}" target="_blank" rel="noopener">↗</a>
        </div>
      </td>
    `;

    fragment.appendChild(tr);
  }

  tbody.appendChild(fragment);
  updatePaginationUI(totalPages);
}

function updatePaginationUI(totalPages) {
  document.getElementById("page-info").textContent = `Page ${currentPage} of ${totalPages}`;
  document.getElementById("prev-btn").disabled = currentPage <= 1;
  document.getElementById("next-btn").disabled = currentPage >= totalPages;
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

function setupEventListeners() {
  const table = document.getElementById("matches-table");
  const tooltip = document.getElementById("tooltip");

  // Pagination clicks
  document.getElementById("prev-btn").addEventListener("click", () => {
    if (currentPage > 1) {
      currentPage--;
      renderTable();
      window.scrollTo({ top: 0, behavior: "smooth" });
    }
  });

  document.getElementById("next-btn").addEventListener("click", () => {
    const totalPages = Math.ceil(allMatches.length / PAGE_SIZE);
    if (currentPage < totalPages) {
      currentPage++;
      renderTable();
      window.scrollTo({ top: 0, behavior: "smooth" });
    }
  });

  // Delegated Copy Match ID
  table.addEventListener("click", (e) => {
    const copyTarget = e.target.closest(".copy-id");
    if (copyTarget) {
      const mid = copyTarget.dataset.mid;
      navigator.clipboard.writeText(mid).then(() => {
        const originalText = copyTarget.textContent;
        copyTarget.textContent = "Copied!";
        setTimeout(() => (copyTarget.textContent = originalText), 1000);
      });
    }
  });

  // Delegated Tooltips
  table.addEventListener("mouseover", (e) => {
    const target = e.target.closest("[data-tooltip]");
    if (target) {
      tooltip.textContent = target.dataset.tooltip;
      tooltip.classList.remove("hidden");
    }
  });

  table.addEventListener("mousemove", (e) => {
    if (!tooltip.classList.contains("hidden")) {
      tooltip.style.left = `${e.clientX + 10}px`;
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
