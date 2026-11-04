const STEAM_CDN = "https://cdn.cloudflare.steamstatic.com";
const PAGE_SIZE = 20;
const numberFormatter = new Intl.NumberFormat("en-US");

let allMatches = [];
let matchesMap = new Map();
let heroesMap = {};
let itemsMap = {};
let currentPage = 1;

async function init() {
  try {
    const [heroesRes, itemsRes, matchesRes] = await Promise.all([
      fetch("../constants/heroes.json"),
      fetch("../constants/items.json"),
      fetch("../data/matches.json")
    ]);

    heroesMap = await heroesRes.json();
    itemsMap = await itemsRes.json();
    allMatches = await matchesRes.json();
    matchesMap = new Map(allMatches.map(m => [Number(m.gc.match_id), m]));

    const countEl = document.getElementById("total-entries-count");
    if (countEl) countEl.textContent = numberFormatter.format(allMatches.length);

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
    const dateStr = formatTimestampDate(dateObj); // e.g. 31/10/2026
    const timeStr = dateObj.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hour12: false });

    // Hero Topbar Portrait
    const hero = heroesMap[m.gc.hero_id] || { localized_name: "Unknown", img: "", icon: "" };
    const heroImgUrl = hero.img ? `${STEAM_CDN}${hero.img}` : "";

    // End MMR & Change
    let mmrDisplay = "";

    if (m.gc.is_calibration || m.gc.start_mmr === null) {
      mmrDisplay = `<span class="calibration-text">Calibration</span>`;
    } else {
      const endMmr = m.gc.start_mmr + (m.gc.rank_change || 0);
      let changeDisplay = "";

      if (m.gc.rank_change > 0) {
        changeDisplay = `<span class="val-positive">+${m.gc.rank_change}</span>`;
      } else if (m.gc.rank_change < 0) {
        changeDisplay = `<span class="val-negative">${m.gc.rank_change}</span>`;
      } else {
        changeDisplay = `<span class="val-neutral">0</span>`;
      }

      mmrDisplay = `<div class="mmr-cell"><span class="mmr-val">${endMmr}</span><span class="mmr-change">${changeDisplay}</span></div>`;
    }

    const durationStr = formatDuration(m.opendota?.duration);
    const draftHtml = buildDraftGrid(m.opendota?.players, m.gc.hero_id, m.gc.match_id);
    const lvlHtml = m.opendota?.level != null
      ? `<span class="lvl-badge">${m.opendota.level}</span>`
      : "—";
    const lhdnStr = (m.opendota?.last_hits != null && m.opendota?.denies != null)
      ? `${m.opendota.last_hits}/${m.opendota.denies}`
      : "—";

    const gpmxpmStr = (m.opendota?.gold_per_min != null && m.opendota?.xp_per_min != null)
      ? `${m.opendota.gold_per_min}/${m.opendota.xp_per_min}`
      : "—";

    const myPlayer = m.opendota?.players?.find(p => p.hero_id === m.gc.hero_id);

    // Endgame inventory (6 main items + 3 backpack items), neutral item & Aghs/Shard
    const itemsHtml = buildTableItemsHtml(myPlayer?.items, myPlayer?.backpack);
    const neutralHtml = buildTableNeutralHtml(myPlayer?.neutral_item);
    const aghsHtml = buildTableAghsHtml(myPlayer);

    // Kill Participation (KP) = (my Kills + my Assists) / My team's score
    let kpStr = "—";
    let kpTooltipAttr = "";
    if (m.opendota && m.opendota.kills != null && m.opendota.assists != null && myPlayer) {
      const teamScore = myPlayer.isRadiant ? m.opendota.radiant_score : m.opendota.dire_score;
      if (teamScore != null && teamScore > 0) {
        const personalKp = m.opendota.kills + m.opendota.assists;
        const kpRatio = personalKp / teamScore;
        const kpPercent = Math.round(kpRatio * 100);
        kpStr = `${kpPercent}%`;
        kpTooltipAttr = `data-tooltip="${personalKp}/${teamScore}"`;
      } else if (teamScore === 0) {
        kpStr = "0%";
        kpTooltipAttr = `data-tooltip="0/0"`;
      }
    }
    const kpHtml = kpTooltipAttr ? `<span ${kpTooltipAttr}>${kpStr}</span>` : kpStr;

    // KDA & Dynamic Ratio Line
    const kVal = m.opendota?.kills;
    const dVal = m.opendota?.deaths;
    const aVal = m.opendota?.assists;
    const hasKda = kVal != null && dVal != null && aVal != null;
    let barSegmentsHtml = "";
    if (hasKda) {
      const totalKda = kVal + dVal + aVal;
      if (totalKda > 0) {
        const kPct = ((kVal / totalKda) * 100).toFixed(1);
        const dPct = ((dVal / totalKda) * 100).toFixed(1);
        const aPct = (100 - parseFloat(kPct) - parseFloat(dPct)).toFixed(1);
        if (kVal > 0) barSegmentsHtml += `<div class="kda-bar-k" style="width:${kPct}%;"></div>`;
        if (dVal > 0) barSegmentsHtml += `<div class="kda-bar-d" style="width:${dPct}%;"></div>`;
        if (aVal > 0) barSegmentsHtml += `<div class="kda-bar-a" style="width:${aPct}%;"></div>`;
      }
    }
    const kdaHtml = `
      <div class="kda-cell">
        <div class="kda-values">
          <span class="kda-k">${kVal ?? "—"}</span>
          <span class="kda-d">${dVal ?? "—"}</span>
          <span class="kda-a">${aVal ?? "—"}</span>
        </div>
        <div class="kda-bar">${barSegmentsHtml}</div>
      </div>
    `;

    tr.innerHTML = `
      <td class="col-timestamp group-end">${dateStr} ${timeStr}</td>
      <td class="col-hero group-end">
        <img class="hero-portrait" src="${heroImgUrl}" alt="${hero.localized_name}" 
             data-tooltip="${hero.localized_name}" loading="lazy" decoding="async">
      </td>
      <td class="col-mmr group-end">${mmrDisplay}</td>
      <td class="col-duration group-end">${durationStr}</td>
      <td class="col-draft group-end">${draftHtml}</td>
      <td class="col-lvl group-end">${lvlHtml}</td>
      <td class="col-kda group-end">${kdaHtml}</td>
      <td class="col-kp group-end">${kpHtml}</td>
      <td class="col-lhdn group-end">${lhdnStr}</td>
      <td class="col-net group-end">${m.opendota?.net_worth != null ? numberFormatter.format(m.opendota.net_worth) : "—"}</td>
      <td class="col-gpmxpm group-end">${gpmxpmStr}</td>
      <td class="col-items">${itemsHtml}</td>
      <td class="col-neutral">${neutralHtml}</td>
      <td class="col-aghs group-end">${aghsHtml}</td>
      <td class="col-match">
        <div class="match-cell">
          <span class="copy-id" data-mid="${m.gc.match_id}" data-tooltip="Click to copy">${m.gc.match_id}<span class="copied-toast hidden">Copied!</span></span>
          <a class="external-link" href="https://www.opendota.com/matches/${m.gc.match_id}" target="_blank" rel="noopener" data-tooltip="View on OpenDota">↗</a>
        </div>
      </td>
    `;

    fragment.appendChild(tr);
  }

  tbody.appendChild(fragment);
  updatePaginationUI(totalPages, totalMatches);
}

function updatePaginationUI(totalPages, totalMatches) {
  document.querySelectorAll(".page-num-display").forEach(el => {
    el.textContent = currentPage;
  });

  document.querySelectorAll(".page-num-input").forEach(el => {
    el.value = currentPage;
    el.max = totalPages;
  });

  document.querySelectorAll(".total-pages-display").forEach(el => {
    el.textContent = totalPages;
  });

  const isFirst = currentPage <= 1;
  const isLast = currentPage >= totalPages;

  document.querySelectorAll(".btn-first").forEach(btn => btn.disabled = isFirst);
  document.querySelectorAll(".btn-prev").forEach(btn => btn.disabled = isFirst);
  document.querySelectorAll(".btn-next").forEach(btn => btn.disabled = isLast);
  document.querySelectorAll(".btn-last").forEach(btn => btn.disabled = isLast);

  if (totalMatches != null) {
    const formatted = numberFormatter.format(totalMatches);
    document.querySelectorAll(".total-entries-count").forEach(el => {
      el.textContent = formatted;
    });
  }
}

function formatTimestampDate(dateObj) {
  const day = String(dateObj.getDate()).padStart(2, "0");
  const month = String(dateObj.getMonth() + 1).padStart(2, "0");
  const year = dateObj.getFullYear();
  return `${day}/${month}/${year}`;
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

function buildTableItemsHtml(mainItems, backpackItems) {
  const main = Array.isArray(mainItems) ? mainItems : [];
  const backpack = Array.isArray(backpackItems) ? backpackItems : [];

  let html = `<div class="table-items-container">`;

  // 6 Main inventory items (39x28px, 2px gap)
  html += `<div class="table-items-main">`;
  for (let i = 0; i < 6; i++) {
    const itemId = main[i];
    if (!itemId || itemId === 0) {
      html += `<div class="table-item-slot empty"></div>`;
    } else {
      const item = itemsMap[itemId] || { name: `Item #${itemId}`, img: "" };
      const iconUrl = item.img ? `${STEAM_CDN}${item.img}` : "";
      html += `
        <div class="table-item-slot" data-tooltip="${item.name}">
          ${iconUrl ? `<img class="item-img" src="${iconUrl}" alt="${item.name}" loading="lazy" decoding="async">` : ""}
        </div>
      `;
    }
  }
  html += `</div>`;

  // 3 Backpack items (26x19px, 2px gap)
  html += `<div class="table-items-backpack">`;
  for (let i = 0; i < 3; i++) {
    const itemId = backpack[i];
    if (!itemId || itemId === 0) {
      html += `<div class="table-item-slot backpack empty"></div>`;
    } else {
      const item = itemsMap[itemId] || { name: `Item #${itemId}`, img: "" };
      const iconUrl = item.img ? `${STEAM_CDN}${item.img}` : "";
      html += `
        <div class="table-item-slot backpack" data-tooltip="${item.name}">
          ${iconUrl ? `<img class="item-img" src="${iconUrl}" alt="${item.name}" loading="lazy" decoding="async">` : ""}
        </div>
      `;
    }
  }
  html += `</div>`;

  html += `</div>`;
  return html;
}

function buildTableNeutralHtml(neutralItemId) {
  if (!neutralItemId || neutralItemId === 0) {
    return `<div class="table-neutral-slot empty"></div>`;
  }
  const item = itemsMap[neutralItemId] || { name: `Item #${neutralItemId}`, img: "" };
  const iconUrl = item.img ? `${STEAM_CDN}${item.img}` : "";
  return `
    <div class="table-neutral-slot" data-tooltip="${item.name}">
      ${iconUrl ? `<img class="item-img" src="${iconUrl}" alt="${item.name}" loading="lazy" decoding="async">` : ""}
    </div>
  `;
}

function buildTableAghsHtml(player) {
  const hasAghs = player?.aghanims_scepter === 1;
  const hasShard = player?.aghanims_shard === 1;

  const scepterImg = hasAghs ? "../assets/aghanims/scepter_1.png" : "../assets/aghanims/scepter_0.png";
  const scepterTooltip = "Aghanim's Blessing";

  const shardImg = hasShard ? "../assets/aghanims/shard_1.png" : "../assets/aghanims/shard_0.png";
  const shardTooltip = "Aghanim's Shard";

  return `
    <div class="inv-aghs-stack">
      <div class="upgrade-slot scepter ${hasAghs ? 'lit' : 'unlit'}" data-tooltip="${scepterTooltip}">
        <img src="${scepterImg}" alt="${scepterTooltip}" loading="lazy" decoding="async">
      </div>
      <div class="upgrade-slot shard ${hasShard ? 'lit' : 'unlit'}" data-tooltip="${shardTooltip}">
        <img src="${shardImg}" alt="${shardTooltip}" loading="lazy" decoding="async">
      </div>
    </div>
  `;
}

function buildDraftGrid(players, myHeroId, matchId) {
  if (!players || players.length === 0) return "—";

  const dire = players.filter(p => !p.isRadiant);
  const radiant = players.filter(p => p.isRadiant);
  const ordered = [...dire, ...radiant];

  let html = `<div class="draft-grid">`;
  for (const p of ordered) {
    const hero = heroesMap[p.hero_id] || { localized_name: `Hero ${p.hero_id}`, icon: "" };
    const iconUrl = hero.icon ? `${STEAM_CDN}${hero.icon}` : "";
    const isSelf = p.hero_id === myHeroId;

    html += `
      <img class="draft-icon ${isSelf ? 'self' : ''}" 
           src="${iconUrl}" 
           data-draft-match="${matchId}"
           data-draft-hero="${p.hero_id}"
           loading="lazy" 
           decoding="async" 
           alt="${hero.localized_name}">
    `;
  }
  html += `</div>`;
  return html;
}

function buildPlayerCardHtml(matchId, heroId) {
  const match = matchesMap.get(matchId);
  if (!match || !match.opendota?.players) return "";

  const p = match.opendota.players.find(pl => pl.hero_id === heroId);
  if (!p) return "";

  const hero = heroesMap[p.hero_id] || { localized_name: `Hero #${p.hero_id}`, img: "" };
  const heroImgUrl = hero.img ? `${STEAM_CDN}${hero.img}` : "";
  const faction = p.isRadiant ? "Radiant" : "Dire";
  const accHtml = p.account_id
    ? `<span class="player-card-acc copy-acc-id" data-account-id="${p.account_id}" data-tooltip="Click to copy">ID: ${p.account_id}<span class="copied-toast hidden">Copied!</span></span>`
    : `<span class="player-card-acc anonymous">Anonymous</span>`;

  // 6 main inventory items
  const mainItems = Array.isArray(p.items) ? p.items : [];
  let mainItemsHtml = "";
  for (let i = 0; i < 6; i++) {
    mainItemsHtml += renderItemSlot(mainItems[i]);
  }

  // 3 backpack items (~33% smaller)
  const backpack = Array.isArray(p.backpack) ? p.backpack : [];
  let backpackHtml = "";
  for (let i = 0; i < 3; i++) {
    backpackHtml += renderItemSlot(backpack[i], true);
  }

  // 1 neutral item (circular, no cyan border)
  const neutralHtml = renderItemSlot(p.neutral_item, false, true);

  // Aghanim's Scepter & Shard (Stacked to the right of neutral item)
  const hasAghs = p.aghanims_scepter === 1;
  const hasShard = p.aghanims_shard === 1;
  const hasMoonshard = p.moonshard === 1;

  const scepterImg = hasAghs ? "../assets/aghanims/scepter_1.png" : "../assets/aghanims/scepter_0.png";
  const scepterTooltip = "Aghanim's Blessing";

  const shardImg = hasShard ? "../assets/aghanims/shard_1.png" : "../assets/aghanims/shard_0.png";
  const shardTooltip = "Aghanim's Shard";

  const aghsStackHtml = `
    <div class="inv-aghs-stack">
      <div class="upgrade-slot scepter ${hasAghs ? 'lit' : 'unlit'}" data-tooltip="${scepterTooltip}">
        <img src="${scepterImg}" alt="Aghanim's Scepter">
      </div>
      <div class="upgrade-slot shard ${hasShard ? 'lit' : 'unlit'}" data-tooltip="${shardTooltip}">
        <img src="${shardImg}" alt="Aghanim's Shard">
      </div>
    </div>
  `;

  // Optional Moon Shard buff in backpack row (0px box expansion)
  let moonshardHtml = "";
  if (hasMoonshard && itemsMap[247]?.img) {
    moonshardHtml = `
      <div class="buff-slot moonshard" data-tooltip="Moon Shard (Consumed Buff)">
        <img src="${STEAM_CDN}${itemsMap[247].img}" alt="Moon Shard">
      </div>
    `;
  }

  return `
    <div class="player-card">
      <div class="player-card-header">
        ${heroImgUrl ? `<img class="player-card-hero-img" src="${heroImgUrl}" alt="${hero.localized_name}">` : ""}
        <div class="player-card-info">
          <div class="player-card-hero-name">${hero.localized_name}</div>
          <div class="player-card-sub">
            <span class="player-card-faction ${faction.toLowerCase()}">${faction}</span>
            ${accHtml}
          </div>
        </div>
      </div>
      <div class="player-card-body">
        <div class="inv-main-area">
          <div class="inv-main-grid">${mainItemsHtml}</div>
          <div class="inv-neutral-box">${neutralHtml}</div>
          ${aghsStackHtml}
        </div>
        <div class="inv-sub-area">
          <div class="inv-backpack-grid">${backpackHtml}</div>
          ${moonshardHtml}
        </div>
      </div>
    </div>
  `;
}

function renderItemSlot(itemId, isBackpack = false, isNeutral = false) {
  const slotClasses = ["item-slot"];
  if (isBackpack) slotClasses.push("backpack");
  if (isNeutral) slotClasses.push("neutral");

  if (!itemId || itemId === 0) {
    slotClasses.push("empty");
    return `<div class="${slotClasses.join(" ")}"></div>`;
  }

  const item = itemsMap[itemId] || { name: `Item #${itemId}`, img: "" };
  const iconUrl = item.img ? `${STEAM_CDN}${item.img}` : "";

  return `
    <div class="${slotClasses.join(" ")}" data-tooltip="${item.name}">
      ${iconUrl ? `<img class="item-img" src="${iconUrl}" alt="${item.name}">` : ""}
    </div>
  `;
}

function setupEventListeners() {
  const table = document.getElementById("matches-table");
  const tooltip = document.getElementById("tooltip");
  const popover = document.getElementById("player-card-popover");

  const HOVER_INTENT_DELAY = 160; // ms: ignores quick swipes across the grid
  let popoverHideTimer = null;
  let popoverOpenTimer = null;
  let activeDraftHero = null;

  function showPopover(target, mid, hid) {
    clearTimeout(popoverHideTimer);
    clearTimeout(popoverOpenTimer);

    activeDraftHero = hid;

    const cardHtml = buildPlayerCardHtml(mid, hid);
    if (!cardHtml) return;

    popover.innerHTML = cardHtml;
    popover.classList.remove("hidden");

    // Position anchored to target icon
    const iconRect = target.getBoundingClientRect();
    const popRect = popover.getBoundingClientRect();
    const grid = target.closest(".draft-grid");
    const pad = 6;

    // Check if target is in the top or bottom half of the 2x5 grid
    let preferAbove = false;
    if (grid) {
      const gridRect = grid.getBoundingClientRect();
      const gridCenterY = gridRect.top + gridRect.height / 2;
      preferAbove = (iconRect.top + iconRect.height / 2) < gridCenterY;
    }

    // Horizontal centering & clamping
    let left = iconRect.left + (iconRect.width / 2) - (popRect.width / 2);
    if (left + popRect.width > window.innerWidth - 12) {
      left = window.innerWidth - popRect.width - 12;
    }
    if (left < 12) {
      left = 12;
    }

    // Vertical positioning:
    // Top row opens ABOVE so travel path doesn't cross the bottom row!
    // Bottom row opens BELOW so travel path doesn't cross the top row!
    let top;
    if (preferAbove) {
      top = iconRect.top - popRect.height - pad;
      // If not enough room above, flip below
      if (top < 12) {
        top = iconRect.bottom + pad;
      }
    } else {
      top = iconRect.bottom + pad;
      // If not enough room below, flip above
      if (top + popRect.height > window.innerHeight - 12) {
        top = iconRect.top - popRect.height - pad;
      }
    }

    // Viewport bounds clamp
    if (top < 12) top = 12;
    if (top + popRect.height > window.innerHeight - 12) {
      top = window.innerHeight - popRect.height - 12;
    }

    popover.style.left = `${Math.round(left)}px`;
    popover.style.top = `${Math.round(top)}px`;
  }

  function schedulePopoverHide() {
    clearTimeout(popoverHideTimer);
    clearTimeout(popoverOpenTimer);
    if (!popover.classList.contains("hidden")) {
      popoverHideTimer = setTimeout(() => {
        popover.classList.add("hidden");
        activeDraftHero = null;
      }, 500); // 500ms grace period
    }
  }

  // Popover mouseenter / mouseleave for persistence
  popover.addEventListener("mouseenter", () => {
    clearTimeout(popoverHideTimer);
    clearTimeout(popoverOpenTimer);
  });

  popover.addEventListener("mouseleave", () => {
    schedulePopoverHide();
  });

  // Copy player ID on click with dedicated "Copied!" popup
  popover.addEventListener("click", (e) => {
    const copyTarget = e.target.closest(".copy-acc-id");
    if (copyTarget && copyTarget.dataset.accountId) {
      const accId = copyTarget.dataset.accountId;
      navigator.clipboard.writeText(accId).then(() => {
        // Hide the "Click to copy" floating tooltip so it doesn't collide
        tooltip.classList.add("hidden");

        const toast = copyTarget.querySelector(".copied-toast");
        if (toast) {
          toast.classList.remove("hidden");
          clearTimeout(copyTarget._toastTimer);
          copyTarget._toastTimer = setTimeout(() => {
            toast.classList.add("hidden");
          }, 1200);
        }
      });
    }
  });

  // Pagination navigation clicks (synced across top and bottom controls)
  document.querySelectorAll(".btn-first").forEach(btn => {
    btn.addEventListener("click", () => {
      if (currentPage > 1) {
        currentPage = 1;
        renderTable();
        window.scrollTo({ top: 0, behavior: "smooth" });
      }
    });
  });

  document.querySelectorAll(".btn-prev").forEach(btn => {
    btn.addEventListener("click", () => {
      if (currentPage > 1) {
        currentPage--;
        renderTable();
        window.scrollTo({ top: 0, behavior: "smooth" });
      }
    });
  });

  document.querySelectorAll(".btn-next").forEach(btn => {
    btn.addEventListener("click", () => {
      const totalPages = Math.ceil(allMatches.length / PAGE_SIZE) || 1;
      if (currentPage < totalPages) {
        currentPage++;
        renderTable();
        window.scrollTo({ top: 0, behavior: "smooth" });
      }
    });
  });

  document.querySelectorAll(".btn-last").forEach(btn => {
    btn.addEventListener("click", () => {
      const totalPages = Math.ceil(allMatches.length / PAGE_SIZE) || 1;
      if (currentPage < totalPages) {
        currentPage = totalPages;
        renderTable();
        window.scrollTo({ top: 0, behavior: "smooth" });
      }
    });
  });

  // Direct page number jump (click to enter number, synced across toolbars)
  const handlePageJump = (rawVal) => {
    const totalPages = Math.ceil(allMatches.length / PAGE_SIZE) || 1;
    const val = parseInt(rawVal.trim(), 10);
    if (!isNaN(val)) {
      const clamped = Math.max(1, Math.min(val, totalPages));
      if (clamped !== currentPage) {
        currentPage = clamped;
        renderTable();
        window.scrollTo({ top: 0, behavior: "smooth" });
      }
    }
    document.querySelectorAll(".page-num-input").forEach(input => input.classList.add("hidden"));
    document.querySelectorAll(".page-num-display").forEach(disp => disp.classList.remove("hidden"));
    updatePaginationUI(totalPages, allMatches.length);
  };

  document.querySelectorAll(".page-num-display").forEach(disp => {
    disp.addEventListener("click", () => {
      const parent = disp.closest(".page-info");
      const input = parent?.querySelector(".page-num-input");
      if (input) {
        disp.classList.add("hidden");
        input.classList.remove("hidden");
        input.value = currentPage;
        input.focus();
        input.select();
        tooltip?.classList.add("hidden");
      }
    });
  });

  document.querySelectorAll(".page-num-input").forEach(input => {
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") {
        e.preventDefault();
        handlePageJump(input.value);
      } else if (e.key === "Escape") {
        e.preventDefault();
        document.querySelectorAll(".page-num-input").forEach(inp => inp.classList.add("hidden"));
        document.querySelectorAll(".page-num-display").forEach(disp => disp.classList.remove("hidden"));
      }
    });

    input.addEventListener("blur", () => {
      handlePageJump(input.value);
    });
  });

  // Delegated Copy Match ID with dedicated "Copied!" popup
  table.addEventListener("click", (e) => {
    const copyTarget = e.target.closest(".copy-id");
    if (copyTarget && copyTarget.dataset.mid) {
      const mid = copyTarget.dataset.mid;
      navigator.clipboard.writeText(mid).then(() => {
        // Hide hover tooltip so it doesn't collide
        tooltip.classList.add("hidden");

        const toast = copyTarget.querySelector(".copied-toast");
        if (toast) {
          toast.classList.remove("hidden");
          clearTimeout(copyTarget._toastTimer);
          copyTarget._toastTimer = setTimeout(() => {
            toast.classList.add("hidden");
          }, 1200);
        }
      });
    }
  });

  // Draft Grid Hover Events (Table)
  table.addEventListener("mouseover", (e) => {
    const draftTarget = e.target.closest(".draft-icon");
    if (draftTarget && draftTarget.dataset.draftMatch) {
      clearTimeout(popoverHideTimer);
      const mid = Number(draftTarget.dataset.draftMatch);
      const hid = Number(draftTarget.dataset.draftHero);

      // If already showing this hero and card is visible, do nothing
      if (activeDraftHero === hid && !popover.classList.contains("hidden")) {
        return;
      }

      // Always require hover intent delay before opening or switching
      clearTimeout(popoverOpenTimer);
      popoverOpenTimer = setTimeout(() => {
        showPopover(draftTarget, mid, hid);
      }, HOVER_INTENT_DELAY);
    }
  });

  table.addEventListener("mouseout", (e) => {
    const draftTarget = e.target.closest(".draft-icon");
    if (draftTarget) {
      clearTimeout(popoverOpenTimer);
      schedulePopoverHide();
    }
  });

  // Global Tooltip for all [data-tooltip] elements (Hero portraits, items, buffs, copy buttons)
  document.addEventListener("mouseover", (e) => {
    const target = e.target.closest("[data-tooltip]");
    if (target) {
      // If the "Copied!" toast is actively showing, suppress the hover tooltip
      const activeToast = target.querySelector(".copied-toast:not(.hidden)");
      if (activeToast) return;

      tooltip.textContent = target.dataset.tooltip;
      tooltip.classList.remove("hidden");
    }
  });

  document.addEventListener("mousemove", (e) => {
    if (!tooltip.classList.contains("hidden")) {
      tooltip.style.left = `${e.clientX + 10}px`;
      tooltip.style.top = `${e.clientY + 12}px`;
    }
  });

  document.addEventListener("mouseout", (e) => {
    const target = e.target.closest("[data-tooltip]");
    if (target) {
      tooltip.classList.add("hidden");
    }
  });
}

document.addEventListener("DOMContentLoaded", init);
