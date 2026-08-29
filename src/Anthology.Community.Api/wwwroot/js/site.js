(() => {
  "use strict";

  const api = "/api/v1";
  const $ = id => document.getElementById(id);
  const identity = {
    id: localStorage.getItem("anthology-community-user-id") || crypto.randomUUID(),
    name: localStorage.getItem("anthology-community-name") || "Игрок",
  };
  localStorage.setItem("anthology-community-user-id", identity.id);

  let feed = null;
  let selectedChannel = "general";
  let selectedPlayerReport = null;
  let selectedDeveloperReport = null;
  let developerToken = sessionStorage.getItem("anthology-developer-token") || "";
  let chatTimer = null;

  $("nickname").value = identity.name;
  $("developer-token").value = developerToken;
  $("nickname").addEventListener("change", event => {
    identity.name = event.target.value.trim() || "Игрок";
    event.target.value = identity.name;
    localStorage.setItem("anthology-community-name", identity.name);
  });

  document.querySelectorAll(".nav-button").forEach(button => button.addEventListener("click", () => showPage(button.dataset.page)));
  $("chat-form").addEventListener("submit", sendChatMessage);
  $("report-form").addEventListener("submit", submitReport);
  $("player-reply-form").addEventListener("submit", replyAsPlayer);
  $("developer-login").addEventListener("click", developerLogin);
  $("create-backup").addEventListener("click", createBackup);
  $("developer-reply-form").addEventListener("submit", replyAsDeveloper);
  $("set-status").addEventListener("click", setDeveloperStatus);
  $("delete-report").addEventListener("click", deleteDeveloperReport);

  function showPage(page) {
    document.querySelectorAll(".nav-button").forEach(button => button.classList.toggle("active", button.dataset.page === page));
    document.querySelectorAll(".page").forEach(section => section.classList.toggle("active", section.id === `page-${page}`));
    const titles = { community: "Сообщество Anthology", reports: "Баг-репорты", developer: "Панель разработчика" };
    $("page-title").textContent = titles[page] || "A.N.T.H.O.L.O.G.Y";
    if (page === "reports") renderMyReports();
    if (page === "developer" && developerToken) loadDeveloperReports();
  }

  async function request(url, options = {}) {
    const response = await fetch(url, options);
    if (!response.ok) {
      let message = `${response.status} ${response.statusText}`;
      try { const body = await response.json(); message = body.error || message; } catch { }
      throw new Error(message);
    }
    if (response.status === 204) return null;
    return response.json();
  }

  async function checkHealth() {
    try {
      const health = await request("/health");
      $("server-dot").className = "status-dot online";
      $("server-label").textContent = "Сервер на связи";
      $("server-details").textContent = `${health.storage.toUpperCase()} · сообщений ${health.messages} · обращений ${health.reports}`;
    } catch {
      $("server-dot").className = "status-dot offline";
      $("server-label").textContent = "Сервер недоступен";
      $("server-details").textContent = "Проверьте службу Anthology";
    }
  }

  async function loadFeed() {
    try {
      feed = await request(`${api}/feed`);
      renderChannels();
      renderPolls();
      await selectChannel(feed.channels.some(channel => channel.id === selectedChannel) ? selectedChannel : feed.channels[0]?.id);
    } catch (error) { toast(error.message, true); }
  }

  function renderChannels() {
    const root = $("channels"); root.replaceChildren();
    (feed?.channels || []).forEach(channel => {
      const button = document.createElement("button"); button.className = `channel-button${channel.id === selectedChannel ? " active" : ""}`;
      const name = document.createElement("strong"); name.textContent = `# ${channel.name}`;
      const description = document.createElement("small"); description.textContent = channel.description;
      button.append(name, description); button.addEventListener("click", () => selectChannel(channel.id)); root.append(button);
    });
  }

  async function selectChannel(channelId) {
    if (!channelId) return;
    selectedChannel = channelId; renderChannels();
    const channel = feed.channels.find(item => item.id === channelId);
    $("channel-name").textContent = channel?.name || channelId;
    await loadMessages();
    clearInterval(chatTimer); chatTimer = setInterval(loadMessages, 2500);
  }

  async function loadMessages() {
    try {
      const messages = await request(`${api}/channels/${encodeURIComponent(selectedChannel)}/messages`);
      const root = $("messages"); const nearBottom = root.scrollHeight - root.scrollTop - root.clientHeight < 80; root.replaceChildren();
      if (!messages.length) { root.append(empty("Сообщений пока нет. Начните разговор.")); return; }
      messages.forEach(message => root.append(renderMessage(message)));
      if (nearBottom) root.scrollTop = root.scrollHeight;
      $("chat-state").textContent = "НА СВЯЗИ";
    } catch { $("chat-state").textContent = "НЕТ СВЯЗИ"; }
  }

  function renderMessage(message) {
    const row = document.createElement("article"); row.className = "message";
    const avatar = document.createElement("div"); avatar.className = "avatar"; avatar.textContent = (message.authorName || "?").slice(0, 1).toUpperCase();
    const body = document.createElement("div"); const head = document.createElement("div"); head.className = "message-head";
    const author = document.createElement("strong"); author.textContent = message.authorName;
    head.append(author);
    if (message.isDeveloper) { const badge = document.createElement("span"); badge.className = "dev-badge"; badge.textContent = "DEV"; head.append(badge); }
    const time = document.createElement("small"); time.textContent = new Date(message.createdAt).toLocaleString(); head.append(time);
    const text = document.createElement("p"); text.textContent = message.text; body.append(head, text); row.append(avatar, body); return row;
  }

  async function sendChatMessage(event) {
    event.preventDefault(); const input = $("chat-text"); const text = input.value.trim(); if (!text) return;
    try {
      await request(`${api}/channels/${encodeURIComponent(selectedChannel)}/messages`, {
        method: "POST", headers: jsonHeaders(developerToken), body: JSON.stringify({ authorId: identity.id, authorName: identity.name, text })
      });
      input.value = ""; await loadMessages();
    } catch (error) { toast(error.message, true); }
  }

  function renderPolls() {
    const root = $("polls"); root.replaceChildren();
    (feed?.polls || []).forEach(poll => {
      const card = document.createElement("article"); card.className = "poll-card";
      const title = document.createElement("h3"); title.textContent = poll.question; card.append(title);
      poll.options.forEach(option => {
        const button = document.createElement("button"); button.className = "poll-option";
        const label = document.createElement("span"); label.textContent = option.text;
        const votes = document.createElement("span"); votes.textContent = option.votes;
        button.append(label, votes); button.addEventListener("click", () => vote(poll.id, option.id)); card.append(button);
      }); root.append(card);
    });
  }

  async function vote(pollId, optionId) {
    try {
      await request(`${api}/polls/${encodeURIComponent(pollId)}/votes`, { method: "POST", headers: jsonHeaders(), body: JSON.stringify({ userId: identity.id, optionIds: [optionId] }) });
      await loadFeed(); toast("Голос принят.");
    } catch (error) { toast(error.message, true); }
  }

  async function submitReport(event) {
    event.preventDefault();
    const report = {
      title: $("report-title").value.trim(), description: $("report-description").value.trim(), reproductionSteps: $("report-steps").value.trim(),
      expectedResult: $("report-expected").value.trim(), actualResult: $("report-actual").value.trim(), launcherVersion: $("report-launcher-version").value.trim(),
      gameVersion: $("report-game-version").value.trim(), logExcerpt: valueOrNull("report-log"), contact: null, systemSpecs: valueOrNull("report-specs"),
      evidenceUrl: valueOrNull("report-url"), reporterId: identity.id, reporterName: identity.name, interfaceLanguage: "ru"
    };
    try {
      const receipt = await request(`${api}/bug-reports`, { method: "POST", headers: jsonHeaders(), body: JSON.stringify(report) });
      const files = Array.from($("report-files").files);
      if (files.length) {
        const form = new FormData(); files.forEach(file => form.append("files", file));
        await request(`${api}/bug-reports/${encodeURIComponent(receipt.id)}/attachments`, { method: "POST", headers: { "X-Anthology-Report-Token": receipt.accessToken }, body: form });
      }
      const reports = playerReports(); reports[receipt.id] = { token: receipt.accessToken, title: report.title, createdAt: receipt.createdAt };
      localStorage.setItem("anthology-community-reports", JSON.stringify(reports)); event.target.reset();
      $("report-game-version").value = "2.1.131"; $("report-launcher-version").value = "Next";
      renderMyReports(); await openPlayerReport(receipt.id); toast(`Обращение ${receipt.id} создано.`);
    } catch (error) { toast(error.message, true); }
  }

  function playerReports() { try { return JSON.parse(localStorage.getItem("anthology-community-reports") || "{}"); } catch { return {}; } }
  function renderMyReports() {
    const reports = playerReports(); const root = $("my-reports"); root.replaceChildren();
    const entries = Object.entries(reports);
    if (!entries.length) { root.append(empty("У вас ещё нет отправленных обращений.")); return; }
    entries.reverse().forEach(([id, item]) => {
      const button = document.createElement("button"); button.className = `report-item${id === selectedPlayerReport ? " active" : ""}`;
      const title = document.createElement("strong"); title.textContent = item.title || id;
      const meta = document.createElement("small"); meta.textContent = `${id} · ${new Date(item.createdAt).toLocaleString()}`;
      button.append(title, meta); button.addEventListener("click", () => openPlayerReport(id)); root.append(button);
    });
  }

  async function openPlayerReport(id) {
    const item = playerReports()[id]; if (!item) return;
    try {
      const report = await request(`${api}/bug-reports/${encodeURIComponent(id)}`, { headers: { "X-Anthology-Report-Token": item.token } });
      selectedPlayerReport = id; renderMyReports(); renderReportDetail($("my-report-detail"), report, false); $("player-reply-form").classList.remove("hidden");
    } catch (error) { toast(error.message, true); }
  }

  async function replyAsPlayer(event) {
    event.preventDefault(); const item = playerReports()[selectedPlayerReport]; const text = $("player-reply").value.trim(); if (!item || !text) return;
    try {
      await request(`${api}/bug-reports/${encodeURIComponent(selectedPlayerReport)}/messages`, { method: "POST", headers: { ...jsonHeaders(), "X-Anthology-Report-Token": item.token }, body: JSON.stringify({ authorId: identity.id, authorName: identity.name, text, language: "ru" }) });
      $("player-reply").value = ""; await openPlayerReport(selectedPlayerReport);
    } catch (error) { toast(error.message, true); }
  }

  async function developerLogin() {
    developerToken = $("developer-token").value.trim();
    try {
      const status = await request(`${api}/admin/status`, { headers: devHeaders() });
      sessionStorage.setItem("anthology-developer-token", developerToken); $("create-backup").disabled = false;
      $("developer-state").textContent = `Доступ подтверждён · ${status.engine.toUpperCase()} · обращений ${status.reports}`;
      await loadDeveloperReports(); toast("Права разработчика подтверждены.");
    } catch (error) { developerToken = ""; sessionStorage.removeItem("anthology-developer-token"); $("create-backup").disabled = true; toast(`Доступ отклонён: ${error.message}`, true); }
  }

  async function loadDeveloperReports() {
    if (!developerToken) return;
    try {
      const reports = await request(`${api}/bug-reports`, { headers: devHeaders() }); const root = $("developer-reports"); root.replaceChildren();
      if (!reports.length) { root.append(empty("Входящих обращений пока нет.")); return; }
      reports.forEach(report => {
        const button = document.createElement("button"); button.className = `report-item${report.receipt.id === selectedDeveloperReport ? " active" : ""}`;
        const title = document.createElement("strong"); title.textContent = report.report.title;
        const meta = document.createElement("small"); meta.textContent = `${report.receipt.id} · ${report.report.reporterName}`;
        const status = document.createElement("span"); status.className = "status"; status.textContent = report.receipt.status;
        button.append(title, meta, status); button.addEventListener("click", () => openDeveloperReport(report.receipt.id)); root.append(button);
      });
    } catch (error) { toast(error.message, true); }
  }

  async function openDeveloperReport(id) {
    try {
      const report = await request(`${api}/bug-reports/${encodeURIComponent(id)}`, { headers: devHeaders() }); selectedDeveloperReport = id;
      $("developer-report-title").textContent = report.report.title; $("developer-status").value = report.receipt.status;
      renderReportDetail($("developer-report-detail"), report, true); $("developer-reply-form").classList.remove("hidden"); await loadDeveloperReports();
    } catch (error) { toast(error.message, true); }
  }

  async function replyAsDeveloper(event) {
    event.preventDefault(); const text = $("developer-reply").value.trim(); if (!selectedDeveloperReport || !text) return;
    try {
      await request(`${api}/bug-reports/${encodeURIComponent(selectedDeveloperReport)}/messages`, { method: "POST", headers: devHeaders(true), body: JSON.stringify({ authorId: `developer:${identity.id}`, authorName: identity.name, text, language: "ru" }) });
      $("developer-reply").value = ""; await openDeveloperReport(selectedDeveloperReport);
    } catch (error) { toast(error.message, true); }
  }

  async function setDeveloperStatus() {
    if (!selectedDeveloperReport) return;
    try {
      await request(`${api}/bug-reports/${encodeURIComponent(selectedDeveloperReport)}/status`, { method: "PATCH", headers: devHeaders(true), body: JSON.stringify({ status: $("developer-status").value, developerName: identity.name }) });
      await openDeveloperReport(selectedDeveloperReport); toast("Статус обновлён.");
    } catch (error) { toast(error.message, true); }
  }

  async function deleteDeveloperReport() {
    if (!selectedDeveloperReport || !confirm(`Удалить ${selectedDeveloperReport} без возможности восстановления?`)) return;
    try {
      await request(`${api}/bug-reports/${encodeURIComponent(selectedDeveloperReport)}`, { method: "DELETE", headers: devHeaders() });
      selectedDeveloperReport = null; $("developer-report-title").textContent = "Выберите баг-репорт";
      $("developer-report-detail").className = "report-detail empty"; $("developer-report-detail").textContent = "Обращение удалено.";
      $("developer-reply-form").classList.add("hidden"); await loadDeveloperReports(); toast("Обращение удалено.");
    } catch (error) { toast(error.message, true); }
  }

  async function createBackup() {
    try { const result = await request(`${api}/admin/backups`, { method: "POST", headers: devHeaders() }); toast(`Резервная копия создана: ${result.path}`); }
    catch (error) { toast(error.message, true); }
  }

  function renderReportDetail(root, details, developer) {
    root.classList.remove("empty"); root.replaceChildren();
    const title = document.createElement("h3"); title.textContent = details.report.title;
    const status = document.createElement("span"); status.className = "status"; status.textContent = details.receipt.status;
    const dl = document.createElement("dl");
    addDefinition(dl, "Игрок", details.report.reporterName || "Игрок"); addDefinition(dl, "Описание", details.report.description);
    addDefinition(dl, "Воспроизведение", details.report.reproductionSteps); addDefinition(dl, "Ожидалось", details.report.expectedResult);
    addDefinition(dl, "Получено", details.report.actualResult); addDefinition(dl, "Версии", `${details.report.gameVersion} / ${details.report.launcherVersion}`);
    if (details.report.systemSpecs) addDefinition(dl, "ПК", details.report.systemSpecs);
    if (details.report.logExcerpt) addDefinition(dl, "Лог", details.report.logExcerpt);
    if (details.report.evidenceUrl) addDefinition(dl, "Пакет", details.report.evidenceUrl);
    if (details.attachments?.length) addDefinition(dl, "Файлы", details.attachments.map(file => `${file.fileName} (${formatBytes(file.size)})`).join("\n"));
    const thread = document.createElement("div"); thread.className = "thread";
    details.messages.forEach(message => {
      const item = document.createElement("article"); item.className = `thread-message ${message.authorRole}`;
      const head = document.createElement("strong"); head.textContent = `${message.authorName} · ${new Date(message.createdAt).toLocaleString()}`;
      const text = document.createElement("p"); text.textContent = message.text; item.append(head, text); thread.append(item);
    });
    root.append(title, status, dl, thread);
  }

  function addDefinition(dl, term, value) { const dt = document.createElement("dt"); dt.textContent = term; const dd = document.createElement("dd"); dd.textContent = value || "—"; dl.append(dt, dd); }
  function empty(text) { const node = document.createElement("div"); node.className = "empty"; node.textContent = text; return node; }
  function valueOrNull(id) { const value = $(id).value.trim(); return value || null; }
  function jsonHeaders(token) { const headers = { "Content-Type": "application/json" }; if (token) headers["X-Anthology-Developer-Token"] = token; return headers; }
  function devHeaders(json = false) { const headers = { "X-Anthology-Developer-Token": developerToken }; if (json) headers["Content-Type"] = "application/json"; return headers; }
  function formatBytes(value) { if (value < 1024) return `${value} Б`; if (value < 1048576) return `${(value / 1024).toFixed(1)} КБ`; return `${(value / 1048576).toFixed(1)} МБ`; }
  let toastTimer; function toast(message, error = false) { const node = $("toast"); node.textContent = message; node.className = `toast show${error ? " error" : ""}`; clearTimeout(toastTimer); toastTimer = setTimeout(() => node.className = "toast", 4500); }

  checkHealth(); loadFeed(); setInterval(checkHealth, 15000);
  if (developerToken) developerLogin();
})();
