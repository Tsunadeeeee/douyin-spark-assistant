(function () {
    "use strict";

    const DEFAULTS = Object.freeze({
        message: "续个火花 🔥",
        dailyDedup: true,
        includeActive: true,
        includeRekindling: true,
        includeDormant: true,
        includeExpiring: true,
        manualSelection: false,
        contactDays: 30,
        intervalSeconds: 15,
        maxRecipients: 100,
        maxScrollPages: 180,
    });
    const SELECTORS = Object.freeze({
        row: '[data-e2e="conversation-item"], .conversationConversationItemwrapper',
        name: '.conversationConversationItemtitle, [data-e2e="conversation-name"]',
        header: '.RightPanelHeadertitle, [data-e2e="chat-title"]',
        editor: '.messageEditorinputArea[contenteditable="true"], .messageEditorinputArea [contenteditable="true"], [class*="messageMsgInput"] [contenteditable="true"], [data-e2e="message-input"]',
        send: '.messageMsgInputpublishBtn, .e2e-send-msg-btn, [data-e2e="send-message"]',
        badge: '.commonStreakstreakContainer, [data-e2e="streak-status"]',
    });
    const STORE = "douyin-spark-desktop-v1";
    const LOCK = "douyin-spark-desktop-send-v1";
    const norm = value => String(value == null ? "" : value).replace(/\s+/g, " ").trim();
    const cleanMessage = value => String(value == null ? "" : value).replace(/\r\n?/g, "\n").trim();
    const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
    const dateKey = (date = new Date()) => [date.getFullYear(), String(date.getMonth() + 1).padStart(2, "0"), String(date.getDate()).padStart(2, "0")].join("-");

    function builtinToken(value) {
        const text=String(value==null?"":value).trim();
        return /^\[[^\[\]\r\n]{1,40}\]$/.test(text)&&text.slice(1,-1).trim()?text:"";
    }
    function validateConfig(input, requireContent = true) {
        const message = cleanMessage(input.message);
        const contentKind = input.contentKind || "text";
        if (!["text","builtin","custom"].includes(contentKind)) throw new Error("未知消息类型");
        const emoji = input.emoji && typeof input.emoji === "object" ? {kind:input.emoji.kind,key:String(input.emoji.key||""),src:String(input.emoji.src||""),name:String(input.emoji.name||"").slice(0,80)} : null;
        if (requireContent && contentKind === "custom" && (!emoji || emoji.kind!==contentKind || !emoji.key || emoji.key.length>1800 || !/^https:\/\//.test(emoji.src))) throw new Error("请先选择该类型的官方表情");
        if ((requireContent && contentKind !== "custom" && !message) || message.length > 300) throw new Error("输入内容需为 1–300 字符；自带表情以 [名称] 计入内容");
        const intervalSeconds = Number(input.intervalSeconds);
        if (!Number.isFinite(intervalSeconds) || intervalSeconds < 10 || intervalSeconds > 300) {
            throw new Error("发送间隔需为 10–300 秒；间隔并不是免封保证");
        }
        const maxRecipients = Number(input.maxRecipients);
        if (!Number.isInteger(maxRecipients) || maxRecipients < 1 || maxRecipients > 200) throw new Error("单次上限需为 1–200 人");
        const manualSelection = !!input.manualSelection;
        const includeDormant = input.includeDormant !== false;
        const includeExpiring = input.includeExpiring !== false;
        if (!manualSelection && !input.includeActive && !input.includeRekindling && !includeDormant && !includeExpiring) throw new Error("至少选择一种火花状态，或切换为自选好友模式");
        return { ...DEFAULTS, message, contentKind, emoji, builtinTokenVersion:1, intervalSeconds, maxRecipients,
            includeActive: !!input.includeActive, includeRekindling: !!input.includeRekindling,
            includeDormant, includeExpiring, manualSelection, dailyDedup: input.dailyDedup !== false };
    }

    function classifyEvidence({ labels = [], images = [], labelStyle = false } = {}) {
        const tokens = labels.map(norm).filter(Boolean);
        const label = tokens.join(" | ");
        // 仅使用火花状态组件本身，不能用聊天预览、昵称或聊天正文里的“火花”。
        if (tokens.some(t => /(?:火花已消失|重燃已结束|不可恢复|无法重燃|不可重燃)/.test(t))) {
            return { state: "inactive", evidence: label };
        }
        if (tokens.some(t => /(?:(?:即将|快要).{0,8}(?:消失|熄灭|到期)|(?:\d+|[一二三四五六七八九十几]+)\s*(?:天|小时|分钟|日)\s*(?:之?后|内)?.{0,6}(?:消失|熄灭|到期)|(?:消失|熄灭|到期).{0,6}(?:\d+|[一二三四五六七八九十几]+)\s*(?:天|小时|分钟|日)|剩余.{0,5}(?:\d+|[一二三四五六七八九十几]+)\s*(?:天|小时|分钟|日)|还剩.{0,5}(?:\d+|[一二三四五六七八九十几]+)\s*(?:天|小时|分钟|日))/.test(t))) {
            return { state: "expiring", evidence: label };
        }
        if (tokens.some(t => /(?:正在重燃|重燃中|重燃火花中|火花重燃中)/.test(t))) {
            return { state: "rekindling", evidence: label };
        }
        if (tokens.some(t => /(?:可重燃|可以重燃|点击重燃|待重燃|已熄灭|已熄火|未点燃|未点亮|尚未点亮|待点燃|待点亮|火花变灰|灰色火花|重燃失败)/.test(t))) {
            return { state: "dormant", evidence: label };
        }
        if (tokens.some(t => /^(?:有火花|火花已点亮|火花已点燃|已点亮火花|火花持续\s*\d+\s*天|连续聊?天?\s*\d+\s*天)$/.test(t))) {
            return { state: "active", evidence: label };
        }
        // 仅限已确认的火花组件内的官方图标。灰色状态单独标记，不伪称重燃中。
        const activeColor = /(?:chat_days_(?:normal|blue|closeFriend|green|purple)[_.@]|\/flame_icon\/[^/]+\/(?:normal|blue|closeFriend|green|purple)_)/;
        const dormantColor = /(?:chat_days_(?:disable|gray)[_.@]|\/flame_icon\/[^/]+\/gray_)/;
        const knownHost = /^(?:p\d+\.douyinpic\.com|lf\d+-static\.bytednsdoc\.com)$/;
        for (const raw of images) {
            try {
                const url = new URL(raw, "https://www.douyin.com");
                if (!knownHost.test(url.hostname)) continue;
                if (dormantColor.test(decodeURIComponent(url.pathname))) return { state: "dormant", evidence: label || "官网灰色火花图标" };
                if (!labelStyle && activeColor.test(decodeURIComponent(url.pathname)) && tokens.some(t => /^\d+\s*(?:天)?$/.test(t))) {
                    return { state: "active", evidence: "官网非灰色火花图标：" + decodeURIComponent(url.pathname) };
                }
            } catch (_) { /* 未知资源不猜测 */ }
        }
        return { state: "unknown", evidence: label || "没有明确状态文字或已识别图标" };
    }

    function eligible(state, cfg) {
        return (state === "active" && cfg.includeActive) || (state === "rekindling" && cfg.includeRekindling)
            || (state === "dormant" && cfg.includeDormant) || (state === "expiring" && cfg.includeExpiring);
    }

    function attemptKey(owner, recipient) { return JSON.stringify([owner, recipient]); }
    function alreadyAttempted(records, owner, recipient, day = dateKey()) {
        return records[attemptKey(owner, recipient)]?.day === day;
    }

    function contactTimeRange(value, now = Date.now()) {
        const text = norm(value), today = new Date(now);
        const exact = ms => Number.isFinite(ms) && ms > 0 && ms <= now + 300000 ? {start:ms,end:ms,text} : null;
        const day = (year, month, date, hour, minute) => {
            const d = new Date(year, month - 1, date, hour || 0, minute || 0);
            if (d.getFullYear() !== year || d.getMonth() !== month - 1 || d.getDate() !== date || (hour != null && (hour > 23 || minute > 59))) return null;
            const start = d.getTime(), end = hour == null ? new Date(year, month - 1, date + 1).getTime() - 1 : start + 59999;
            return start <= now + 300000 ? {start,end:Math.min(end,now),text} : null;
        };
        if (!text) return null;
        if (/^\d{10}$/.test(text)) return exact(Number(text) * 1000);
        if (/^\d{13}$/.test(text)) return exact(Number(text));
        if (/^\d{4}-\d\d-\d\dT\d\d:\d\d/.test(text)) return exact(Date.parse(text));
        if (/^(刚刚|现在)$/.test(text)) return {start:now-300000,end:now,text};
        let m = /^(\d+)\s*(分钟|小时|天|日)前$/.exec(text);
        if (m) { const unit = {分钟:60000,小时:3600000,天:86400000,日:86400000}[m[2]], end = now-Number(m[1])*unit; return {start:end-unit+1,end,text}; }
        m = /^(今天|昨天|前天)(?:\s*(\d{1,2}):(\d{2}))?$/.exec(text);
        if (m) { const d = new Date(today.getFullYear(),today.getMonth(),today.getDate()-({今天:0,昨天:1,前天:2}[m[1]])); return day(d.getFullYear(),d.getMonth()+1,d.getDate(),m[2] == null ? null : Number(m[2]),m[3] == null ? null : Number(m[3])); }
        m = /^(\d{1,2}):(\d{2})$/.exec(text);
        if (m) return day(today.getFullYear(),today.getMonth()+1,today.getDate(),Number(m[1]),Number(m[2]));
        m = /^(?:周|星期)([日天一二三四五六])$/.exec(text);
        if (m) { const target = {日:0,天:0,一:1,二:2,三:3,四:4,五:5,六:6}[m[1]]; let ago = (today.getDay()-target+7)%7; if (!ago) ago=7; const d=new Date(today.getFullYear(),today.getMonth(),today.getDate()-ago); return day(d.getFullYear(),d.getMonth()+1,d.getDate()); }
        m = /^(?:(\d{4})[年/.-])?(\d{1,2})[月/.-](\d{1,2})日?(?:\s+(\d{1,2}):(\d{2}))?$/.exec(text);
        if (m) return day(m[1] ? Number(m[1]) : today.getFullYear(),Number(m[2]),Number(m[3]),m[4] == null ? null : Number(m[4]),m[5] == null ? null : Number(m[5]));
        return null;
    }
    function contactRecency(values, now = Date.now(), days = 30) {
        const cutoff = now-days*86400000;
        for (const value of values) {
            const range = contactTimeRange(value,now); if (!range) continue;
            const recent = range.start >= cutoff ? "recent" : range.end < cutoff ? "stale" : "unknown";
            return {recentContact:recent,lastContact:range.text,lastContactAt:range.start,contactReason:recent === "recent" ? "近30天联系" : recent === "stale" ? "超过30天未联系" : "联系时间在30天边界，待确认"};
        }
        return {recentContact:"unknown",lastContact:"",lastContactAt:null,contactReason:"联系时间待确认"};
    }
    function relationshipEvidence(labels, hasOfficialStreak = false) {
        const tokens=labels.map(norm).filter(Boolean);
        if (tokens.some(t=>/^(?:未互关|未互相关注|未关注|已关注|关注|回关|已请求|单向关注)$/.test(t))) return {mutualFollow:"no",relationshipReason:"未互相关注"};
        if (tokens.some(t=>/^(?:互相关注|已互关|相互关注|互关)$/.test(t))) return {mutualFollow:"yes",relationshipReason:"互相关注"};
        // 已核对的官方单聊 Streak 组件仅在 FollowEach 关系下显示；不从昵称或正文推断。
        if (hasOfficialStreak) return {mutualFollow:"yes",relationshipReason:"官方单聊火花组件的互关显示条件"};
        return {mutualFollow:"unknown",relationshipReason:"互关状态待确认"};
    }
    function defaultSelection(meta) {
        const reasons=[];
        if (meta.recentContact !== "recent") reasons.push(meta.contactReason || "联系时间待确认");
        if (meta.mutualFollow !== "yes") reasons.push(meta.relationshipReason || "互关状态待确认");
        return {defaultSelected:!reasons.length,defaultReason:reasons.length?reasons.join(" · "):"近30天联系 · 互关"};
    }

    if (typeof document === "undefined" && typeof module !== "undefined") {
        module.exports = { DEFAULTS, SELECTORS, norm, cleanMessage, dateKey, validateConfig,
            classifyEvidence, eligible, attemptKey, alreadyAttempted, contactTimeRange, contactRecency, relationshipEvidence, defaultSelection, builtinToken };
        return;
    }


    if (window.__SparkDesktop) return;
    const boot = __SPARK_BOOT__;
    const dom = (selector, root = document) => Array.from(root.querySelectorAll(selector));
    const visible = el => {
        if (!el?.isConnected) return false;
        const style = getComputedStyle(el), rect = el.getBoundingClientRect();
        return style.display !== "none" && style.visibility !== "hidden" && style.opacity !== "0" && rect.width > 0 && rect.height > 0;
    };
    let state = boot.state, busy = false, cancelled = false, ownerAtStart = "", preview = [], excluded = new Set();
    let selectedIdentities = new Set();
    let requestedConfig = state.config || {...DEFAULTS};
    const pending = new Map();
    let nextRequest = 0;
    function post(type, data, id) {
        window.chrome.webview.postMessage({ channel: "spark-desktop-v1", session: boot.session, type, data, id });
    }
    function hostCall(type, data) {
        const id = String(++nextRequest);
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => { pending.delete(id); reject(stopError("桌面程序没有确认保存记录，已停止")); }, 10000);
            pending.set(id, {resolve, reject, timer});
            post(type, data, id);
        });
    }
    function readStore() { return state; }
    async function writeStore(value) {
        await hostCall("persist", value);
        state = value;
    }
    function log(message) { post("log", {text: message}); }
    function status(message) { post("status", {text: message}); }
    function setBusy(value) { busy = value; post("busy", {value}); }
    function stopError(message) { const error = new Error(message); error.stop = true; return error; }
    function ownerIdentity() {
        const nodes = dom('[data-e2e="user-info"] a[href*="/user/"], a[data-e2e="user-info"][href*="/user/"], [data-e2e="user-avatar"] a[href*="/user/"]');
        const hrefs = [...new Set(nodes.map(el => el.getAttribute("href")).filter(Boolean))];
        return hrefs.length === 1 ? "account:" + new URL(hrefs[0], location.href).pathname : "browser-profile";
    }
    function checkRun() {
        if (cancelled) throw stopError("已停止，未开始的收件人不会发送");
        if (document.hidden) throw stopError("页面转到后台，任务已停止");
        if (!/^\/chat(?:\/|$)/.test(location.pathname)) throw stopError("请先在软件内打开抖音私信页面");
        if (ownerAtStart && ownerIdentity() !== ownerAtStart) throw stopError("登录身份发生变化，任务已停止");
        const alerts = dom('[role="alert"], [role="dialog"], .semi-toast, .commonToastinfo, .commonToasterror, .commonToastwarning, [class*="Toastcontainer"], [class*="toast-content"]')
            .filter(visible).map(el => norm(el.textContent)).filter(t => t.length < 1200);
        if (alerts.some(t => /(?:发送失败|操作频繁|操作过于频繁|账号异常|安全验证|完成验证|验证码|重新登录|请求过于频繁|发送受限|实名认证|私信功能无法使用)/.test(t))) {
            throw stopError("页面出现验证、发送失败或频率限制提示，已停止，不重试");
        }
    }
    async function pause(ms) {
        for (let remaining = ms; remaining > 0; remaining -= Math.min(200, remaining)) {
            checkRun();
            await sleep(Math.min(200, remaining));
        }
        checkRun();
    }
    async function waitFor(probe, timeout = 7000) {
        const end = Date.now() + timeout;
        do { checkRun(); const result = probe(); if (result) return result; await pause(160); } while (Date.now() < end);
        return null;
    }
    function uniqueVisible(selector, root = document) {
        const found = dom(selector, root).filter(visible);
        return found.length === 1 ? found[0] : null;
    }
    function badgeEvidence(root) {
        const badges = dom(SELECTORS.badge, root).filter(visible);
        if (badges.length === 0) return { state: "none", evidence: "当前会话未显示火花标记" };
        if (badges.length !== 1) return { state: "unknown", evidence: "状态组件不唯一" };
        const badge = badges[0];
        const labels = [badge.textContent, badge.getAttribute("title"), badge.getAttribute("aria-label")];
        dom('[title], [aria-label], img[alt]', badge).forEach(el => labels.push(el.getAttribute("title"), el.getAttribute("aria-label"), el.getAttribute("alt")));
        return classifyEvidence({ labels, images: dom("img", badge).map(el => el.currentSrc || el.src),
            labelStyle: !!badge.querySelector(".commonStreaklabelText") });
    }
    function snapshotRow(row) {
        const title = row.querySelector(SELECTORS.name);
        const name = norm(title?.textContent);
        const group = !!row.querySelector(".commonConversationIconnoDrag") || row.getAttribute("data-conversation-type") === "group";
        const avatar = dom("img", row).find(el => !el.closest(SELECTORS.badge));
        let avatarKey = "";
        try { if (avatar?.src) avatarKey = new URL(avatar.currentSrc || avatar.src, location.href).pathname; } catch (_) { /* skip */ }
        const id = row.getAttribute("data-conversation-id") || row.getAttribute("data-peer-id") || "";
        const identity = id ? "id:" + id : name && avatarKey ? JSON.stringify([name, avatarKey]) : "";
        const time = row.querySelector('.ConversationItemTagNextToTitletimeStr, [data-e2e="conversation-time"], time');
        const timing = contactRecency(time ? [time.getAttribute("datetime"),time.getAttribute("data-timestamp"),time.getAttribute("title"),time.textContent] : []);
        const labels=dom('[data-e2e="follow-status"], [data-e2e="relationship-tag"]',row).filter(visible).map(el=>el.textContent);
        const flag=row.getAttribute("data-mutual-follow"); if (flag === "true") labels.push("互相关注"); else if (flag === "false") labels.push("未互关");
        const relation=relationshipEvidence(labels,!group && dom('.commonStreakstreakContainer',row).filter(visible).length===1);
        const meta={...timing,...relation};
        return { name, identity, group, avatarKey, ...badgeEvidence(row), ...meta, ...defaultSelection(meta), row };
    }
    function rows() { return dom(SELECTORS.row).filter(visible); }
    function getScroller() {
        const first = rows()[0];
        if (!first) throw stopError("未找到会话列表；请先在软件内登录抖音并打开私信");
        for (let el = first.parentElement; el && el !== document.body; el = el.parentElement) {
            if (el.scrollHeight > el.clientHeight + 5 && /auto|scroll/.test(getComputedStyle(el).overflowY)) return el;
        }
        return null;
    }
    async function traverse(visitor, maxPages) {
        checkRun();
        const scroller = getScroller();
        if (scroller) { scroller.scrollTop = 0; scroller.dispatchEvent(new Event("scroll")); await pause(700); }
        let stableEnd = 0, previousEnd = "";
        for (let page = 0; page < maxPages; page++) {
            checkRun();
            const current = rows().map(snapshotRow);
            const result = visitor(current, page);
            if (result) return { found: result, complete: false };
            if (!scroller) return { complete: true };
            if (!scroller.isConnected) throw stopError("会话列表发生重建，请重新运行");
            const atBottom = scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 4;
            if (atBottom) {
                const signature = scroller.scrollHeight + ":" + current.map(item => item.identity).join("|");
                stableEnd = signature === previousEnd ? stableEnd + 1 : 0;
                previousEnd = signature;
                if (stableEnd >= 3) return { complete: true };
                await pause(700);
            } else {
                stableEnd = 0;
                scroller.scrollTop = Math.min(scroller.scrollTop + Math.max(80, scroller.clientHeight * 0.65), scroller.scrollHeight - scroller.clientHeight);
                scroller.dispatchEvent(new Event("scroll"));
                await pause(500);
            }
        }
        return { complete: false };
    }
    function renderPreview(items) {
        post("preview", items.map(({row,...item}) => item));
    }
    async function restoreListTop() {
        try {
            const scroller=getScroller();
            if (scroller) for(let n=0;n<3;n++){scroller.scrollTop=0;scroller.dispatchEvent(new Event("scroll"));await sleep(80);}
        } catch (_) { /* 页面关闭/列表重建时不产生后续点击 */ }
    }
    async function enrichRelationship(item,cfg) {
        if(item.mutualFollow !== "unknown" || item.recentContact !== "recent") return;
        // 只打开聊天和资料卡读取显示状态；绝不点击关注/取消关注按钮。
        try {
            const target=await locate(item,cfg);target.row.scrollIntoView({block:"nearest"});
            for(const type of ["mousedown","mouseup","click"]) target.row.dispatchEvent(new MouseEvent(type,{bubbles:true,cancelable:true,view:window,button:0}));
            const header=await waitFor(()=>{
                const title=uniqueVisible(SELECTORS.header);
                return title && norm(title.textContent)===item.name && selectedRow()?.identity===item.identity ? title : null;
            },2500);
            if(!header)return;
            const right=panelRoot();if(!right)return;
            const readRelation=scope=>relationshipEvidence(dom('.RightPanelHeaderfollow, [class*="UserProfileCardcontentleftButton"], [data-e2e="follow-status"]',scope).filter(visible).map(el=>el.textContent));
            let relation=readRelation(right);
            if(relation.mutualFollow === "unknown") {
                const trigger=uniqueVisible('.RightPanelHeaderuser [data-apm-action="个人页卡片"]',right);
                if(trigger) {
                    trigger.dispatchEvent(new MouseEvent("click",{bubbles:true,cancelable:true,view:window}));
                    const card=await waitFor(()=>uniqueVisible('.UserProfileCardcontentbox'),1800);
                    if(card) {
                        const title=uniqueVisible('.UserProfileCardcontenttitle',card);
                        if(title&&norm(title.textContent)===item.name)relation=readRelation(card);
                    }
                    // 点击已核对的会话行关闭卡片，不操作资料卡按钮。
                    const current=rows().map(snapshotRow).find(row=>row.identity===item.identity);
                    if(current) for(const type of ["mousedown","mouseup","click"]) current.row.dispatchEvent(new MouseEvent(type,{bubbles:true,cancelable:true,view:window,button:0}));
                }
            }
            Object.assign(item,relation,defaultSelection({...item,...relation}));
        } catch(error) {if(error.stop)throw error;}
    }
    async function scan(cfg) {
        const found = new Map(), names = new Map(), duplicates = new Set();
        status("正在滚动扫描会话列表；暂不发送消息…");
        let result;
        try { result = await traverse(items => {
            const local = new Set();
            for (const item of items) {
                if (!item.name) continue;
                if (local.has(item.name)) duplicates.add(item.name);
                local.add(item.name);
                if (!names.has(item.name)) names.set(item.name, new Set());
                names.get(item.name).add(item.identity);
                if (item.identity) found.set(item.identity, { ...item, row: undefined });
            }
            status("扫描中：已读取 " + found.size + " 个会话…");
        }, cfg.maxScrollPages); } finally { await restoreListTop(); }
        if (!result.complete) throw stopError("达到滚动扫描上限，未能确认扫描到列表底部；本次不发送");
        preview = [...found.values()].filter(item => item.identity && !item.group && (cfg.manualSelection || eligible(item.state, cfg))
            && names.get(item.name).size === 1 && !duplicates.has(item.name));
        if(cfg.manualSelection) {
            try {
                for(let i=0;i<preview.length;i++) {
                    const item=preview[i];
                    if(item.mutualFollow === "unknown" && item.recentContact === "recent") {
                        status("核对互关状态 "+(i+1)+"/"+preview.length+"："+item.name);
                        await enrichRelationship(item,cfg);
                    }
                }
            } finally {await restoreListTop();}
        }
        renderPreview(preview);
        const unknown = [...found.values()].filter(item => item.state === "unknown").length;
        log("扫描 " + found.size + " 个会话，匹配 " + preview.length + " 位；状态不明确 " + unknown + " 个；重名/群聊不处理");
        status("扫描完成：" + preview.length + " 位。" + (cfg.manualSelection ? "默认勾选近30天联系且互关的人；可手动调整。" : "包含所选火花状态；取消勾选可排除好友。"));
        return preview;
    }
    function selectedRow() {
        const selected = rows().filter(el => el.classList.contains("conversationConversationItemcurConversation") || el.getAttribute("aria-selected") === "true");
        return selected.length === 1 ? snapshotRow(selected[0]) : null;
    }
    function panelRoot() {
        const title = uniqueVisible(SELECTORS.header);
        return title?.closest(".componentsRightPanelwrapper") || title?.closest('[data-e2e="chat-panel"]') || null;
    }
    function editorText(el) { return cleanMessage(("value" in el ? el.value : el.innerText).replace(/\u200b/g, "").replace(/\u00a0/g, " ")); }
    function currentEditor() {
        const root = panelRoot();
        return root ? uniqueVisible(SELECTORS.editor, root) : null;
    }
    function assertTarget(target, cfg) {
        checkRun();
        const title = uniqueVisible(SELECTORS.header), active = selectedRow();
        if (!title || norm(title.textContent) !== target.name || active?.identity !== target.identity) throw stopError("当前聊天身份与目标不一致，已停止");
        if (active.group) throw stopError("目标是群聊，已停止");
        if (cfg.manualSelection) {
            if (!selectedIdentities.has(target.identity)) throw stopError("目标不在你勾选的名单中，已停止");
            return;
        }
        if (!eligible(active.state, cfg)) throw stopError("目标状态已改变或不再符合筛选，已停止");
        const scope = title.closest(".RightPanelHeadertitleContainer") || title.parentElement;
        if (!eligible(badgeEvidence(scope).state, cfg)) throw stopError("聊天标题区域未确认火花状态，已停止");
    }
    async function locate(target, cfg) {
        const local = rows().map(snapshotRow).filter(item => item.identity === target.identity);
        if (local.length === 1) return local[0];
        const result = await traverse(items => {
            const matches = items.filter(item => item.identity === target.identity);
            return matches.length === 1 ? matches[0] : null;
        }, cfg.maxScrollPages);
        if (!result.found) throw new Error("会话已移动或无法定位，跳过");
        return result.found;
    }
    async function openTarget(target, cfg) {
        const match = await locate(target, cfg);
        if (match.group || (cfg.manualSelection ? !selectedIdentities.has(match.identity) : !eligible(match.state, cfg))) throw new Error("该会话已不符合当前名单，跳过");
        match.row.scrollIntoView({ block: "nearest" });
        checkRun();
        for (const type of ["mousedown", "mouseup", "click"]) match.row.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, view: window, button: 0, buttons: type === "mousedown" ? 1 : 0 }));
        const ready = await waitFor(() => {
            const title = uniqueVisible(SELECTORS.header);
            return title && norm(title.textContent) === target.name && selectedRow()?.identity === target.identity && currentEditor();
        });
        if (!ready) throw stopError("打开聊天失败或身份无法核对，已停止");
        await pause(350);
        assertTarget(target, cfg);
        return currentEditor();
    }
    function fillEditor(input, message) {
        input.focus();
        if (/^(TEXTAREA|INPUT)$/.test(input.tagName)) {
            const proto = input.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
            Object.getOwnPropertyDescriptor(proto, "value").set.call(input, message);
            input.dispatchEvent(new Event("input", { bubbles: true }));
        } else {
            const range = document.createRange(); range.selectNodeContents(input); range.collapse(false);
            const selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range);
            // 使用编辑器支持的可撤销输入，不直接改 innerHTML，不绕过页面发送逻辑。
            if (!document.execCommand("insertText", false, message)) throw stopError("页面编辑器不支持文字输入，未发送");
        }
    }
    async function remember(target, outcome) {
        const state = readStore(); state.records ||= {};
        const now = Date.now();
        Object.keys(state.records).forEach(key => { if (now - state.records[key].at > 35 * 86400000) delete state.records[key]; });
        const key = attemptKey(ownerAtStart, target.identity);
        state.records[key] = { day: dateKey(), at: now, outcome };
        await writeStore(state);
        if (readStore().records?.[key]?.outcome !== outcome) throw stopError("防重复记录写入失败，已停止");
    }
    function emojiKey(src) { try {const u=new URL(src,location.href);u.hash="";return u.protocol === "https:" ? u.href : "";}catch(_){return "";} }
    function editorHasContent(input){return !!editorText(input)||!!input.querySelector("img,video,[data-uuid]");}
    function editorMessage(input) {
        if("value" in input)return cleanMessage(input.value);
        const read=node=>{
            if(node.nodeType===Node.TEXT_NODE)return node.nodeValue||"";
            if(node.nodeType!==Node.ELEMENT_NODE)return "";
            if(node.tagName==="BR")return "\n";
            if(node.tagName==="IMG")return node.getAttribute('data-apm-action')==='输入框emoji'?builtinToken(node.getAttribute('alt'))||"\ufffc":"\ufffc";
            let text="";const children=[...node.childNodes];
            for(let i=0;i<children.length;i++){
                const child=children[i],block=child.nodeType===Node.ELEMENT_NODE&&/^(block|flex|grid|list-item|table-row)$/.test(getComputedStyle(child).display);
                if(block&&text&&!text.endsWith("\n"))text+="\n";
                text+=read(child);
                if(block&&i<children.length-1&&!text.endsWith("\n"))text+="\n";
            }
            return text;
        };
        return cleanMessage(read(input).replace(/\u200b/g,"").replace(/\u00a0/g," "));
    }
    function editorFingerprint(input){return JSON.stringify([editorMessage(input),dom("img",input).map(x=>[emojiKey(x.currentSrc||x.src),x.alt||""])]);}
    function clickNode(node){node.dispatchEvent(new MouseEvent("click",{bubbles:true,cancelable:true,view:window}));}
    async function emojiPanel(kind){
        let panel=uniqueVisible('.componentsemojiemojiPanel');
        if(!panel){const trigger=uniqueVisible('.messageMsgInputiconAction',panelRoot());if(!trigger)throw stopError("当前聊天没有可识别的官方表情入口");clickNode(trigger);panel=await waitFor(()=>uniqueVisible('.componentsemojiemojiPanel'),2500);}
        if(!panel)throw stopError("官方表情面板未打开");
        const tabs=dom('.emojiEmojisModalTabsubTab',panel).filter(visible),index=kind==="builtin"?1:2;
        if(tabs.length<3||tabs[index].classList.contains('emojiEmojisModalTabdisabled'))throw stopError("官方表情分类与适配版本不一致");
        clickNode(tabs[index]);await pause(250);
        const active=()=>tabs[index].isConnected&&(tabs[index].classList.contains('emojiEmojisModalTabactiveDark')||tabs[index].classList.contains('emojiEmojisModalTabactiveLight'));
        if(!await waitFor(active,2000))throw stopError("未确认官方表情分类，未执行发送");
        if(kind==="builtin"&&!await waitFor(()=>uniqueVisible('.emojiSmallEmojismallEmojiPage',panel),2500))throw stopError("官方自带表情未加载");
        return panel;
    }
    function emojiImages(panel,kind){return dom(kind==="builtin"?'.emojiSmallEmojismallEmojiItemIcon':'.emojiEmojiItemimgBox img',panel).filter(visible);}
    function emojiInfo(img,kind,index){const src=img.currentSrc||img.src;return {kind,src,key:emojiKey(src),name:img.alt||img.title||(kind==="custom"?"收藏表情 "+(index+1):""),token:""};}
    async function readBuiltinToken(img) {
        const direct=[img.getAttribute("alt"),img.getAttribute("title"),img.getAttribute("aria-label")].map(builtinToken).find(Boolean);
        if(direct)return direct;
        const item=img.closest('.emojiSmallEmojismallEmojiItem');if(!item)return "";
        img.scrollIntoView({block:"nearest"});await pause(40);
        item.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,view:window,button:2}));
        const key=emojiKey(img.currentSrc||img.src);
        const popup=await waitFor(()=>{
            const candidates=dom('.emojiSmallEmojismallEmojiPopoverItemIcon').filter(visible).filter(x=>emojiKey(x.currentSrc||x.src)===key&&builtinToken(x.getAttribute('title')));
            return candidates.length===1?candidates[0]:null;
        },1000);
        return popup?builtinToken(popup.getAttribute('title')):"";
    }
    async function walkEmojis(kind,findKey){
        const panel=await emojiPanel(kind),scroll=uniqueVisible('.componentsemojitabPanel',panel),found=new Map(),attempted=new Set();
        if(!scroll)throw stopError("表情列表未识别");scroll.scrollTop=0;
        let stable=0,last="";
        for(let pass=0;pass<60;pass++){
            checkRun();await pause(160);
            for(const img of emojiImages(panel,kind)) {
                const info=emojiInfo(img,kind,found.size);if(!info.key)continue;
                if(findKey&&info.key===findKey)return img;
                if(attempted.has(info.key))continue;attempted.add(info.key);
                if(kind==="builtin") {info.token=await readBuiltinToken(img);if(!info.token)continue;info.name=info.token;}
                if(!found.has(info.key))found.set(info.key,info);
            }
            const stamp=found.size+":"+scroll.scrollHeight+":"+Math.round(scroll.scrollTop);stable=stamp===last?stable+1:0;last=stamp;
            if(stable>=3)break;
            scroll.scrollTop=Math.min(scroll.scrollHeight,scroll.scrollTop+Math.max(100,scroll.clientHeight*.8));scroll.dispatchEvent(new Event('scroll'));
        }
        if(findKey)throw stopError("已选表情在当前账号官方面板中找不到，请重新选择");
        if(kind==="builtin"&&attempted.size>found.size)log("未显示 "+(attempted.size-found.size)+" 个名称无法确认的图标；不生成猜测的表情代码");
        return [...found.values()];
    }
    async function loadEmoji(kind){
        if(busy||!["builtin","custom"].includes(kind))return;
        cancelled=false;setBusy(true);ownerAtStart=ownerIdentity();
        try{
            checkRun();const current=selectedRow(),header=uniqueVisible(SELECTORS.header);
            if(!current||current.group||!header||norm(header.textContent)!==current.name)throw stopError("先在左侧打开一位好友的单聊，再读取表情");
            const items=await walkEmojis(kind,null);
            if(selectedRow()?.identity!==current.identity||norm(uniqueVisible(SELECTORS.header)?.textContent)!==current.name)throw stopError("读取时聊天身份改变，未采用表情列表");
            post('emojiCatalog',{kind,items});status("已读取 "+items.length+" 个"+(kind==="builtin"?"自带表情":"收藏表情")+"；选择后插入输入框；读取不会发送");
            const input=currentEditor();if(input)clickNode(input);
        }catch(error){post('emojiCatalog',{kind,items:[],error:error.message});status(error.message);}
        finally{setBusy(false);ownerAtStart="";}
    }
    async function sendCustom(target,cfg,input){
        const img=await walkEmojis('custom',cfg.emoji.key);assertTarget(target,cfg);
        if(editorHasContent(input))throw stopError("存在草稿，未点击表情包");
        const button=img.closest('.emojiEmojiItemimgBox[data-apm-action="EmojiItem"]');
        if(!button)throw stopError("未识别收藏表情发送控件");
        await remember(target,"prepared");assertTarget(target,cfg);checkRun();
        if(editorHasContent(input)||!img.isConnected||emojiKey(img.currentSrc||img.src)!==cfg.emoji.key)throw stopError("发送前表情发生变化，已停止");
        clickNode(button);await remember(target,"clicked");
        const closed=await waitFor(()=>!uniqueVisible('.componentsemojiemojiPanel'),3500);
        if(!closed){await remember(target,"uncertain");throw stopError("表情发送状态不明确，已停止；当天不重试");}
        log("已点击收藏表情发送："+target.name+"（未核验送达/续火成功）");return "clicked";
    }
    async function sendTarget(target, cfg) {
        if (cfg.dailyDedup && alreadyAttempted(readStore().records || {}, ownerAtStart, target.identity)) {
            log("跳过 " + target.name + "：今天已有发送尝试记录"); return "skipped";
        }
        const input = await openTarget(target, cfg);
        if (editorHasContent(input)) { log("跳过 " + target.name + "：保留原有草稿"); return "skipped"; }
        assertTarget(target, cfg);
        if(cfg.contentKind === "custom")return sendCustom(target,cfg,input);
        if(cfg.message)fillEditor(input, cfg.message);
        await pause(400);
        assertTarget(target, cfg);
        if (currentEditor() !== input || editorMessage(input) !== cfg.message) throw stopError("输入文字与表情代码核对失败，已停止；未点击发送");
        const expected=editorFingerprint(input);
        const send = await waitFor(() => {
            const button = uniqueVisible(SELECTORS.send, panelRoot());
            if (!button || button.disabled || button.getAttribute("aria-disabled") === "true") return null;
            if (button.classList.contains("messageMsgInputpublishBtn") && !button.classList.contains("messageMsgInputpublishRedBtn")) return null;
            return button;
        }, 2500);
        if (!send) throw stopError("没有可用发送按钮，消息保留为草稿，已停止");
        assertTarget(target, cfg);
        if (editorFingerprint(input) !== expected) throw stopError("发送前消息内容改变，已停止");
        await remember(target, "prepared");
        assertTarget(target, cfg);
        if(editorFingerprint(input)!==expected)throw stopError("保存记录后输入内容变化，未发送");
        send.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: window }));
        await remember(target, "clicked");
        const cleared = await waitFor(() => { assertTarget(target, cfg); return currentEditor() === input && !editorHasContent(input); }, 5000);
        if (!cleared) { await remember(target, "uncertain"); throw stopError("发送结果不明确，已停止；今天不自动重发"); }
        await remember(target, "composer_cleared");
        log("已点击发送：" + target.name + "（输入框已清空；未核验送达/续火成功）");
        return "clicked";
    }
    function configFromUI(mode) { return validateConfig(requestedConfig,mode === "send"); }
    async function run(mode) {
        if (busy) return;
        let cfg;
        try { cfg = configFromUI(mode); } catch (error) { status(error.message); setBusy(false); return; }
        if (mode === "send" && cfg.manualSelection && !selectedIdentities.size) { status("请先扫描全部会话并勾选至少一位好友；本次未发送。"); setBusy(false); return; }
        if (!navigator.locks?.request) { status("当前浏览器不支持跨标签页运行锁，请更新 Microsoft Edge WebView2 Runtime。"); setBusy(false); return; }
        cancelled = false; setBusy(true); ownerAtStart = ownerIdentity();
        try {
            await navigator.locks.request(LOCK, { ifAvailable: true }, async lock => {
                if (!lock) throw stopError("另一标签页正在运行本助手，当前任务未启动");
                const state = readStore(); state.config = cfg; await writeStore(state);
                const targets = await scan(cfg);
                if (mode === "scan") return;
                const pending = targets.filter(item => (cfg.manualSelection ? selectedIdentities.has(item.identity) : !excluded.has(item.identity)) && (!cfg.dailyDedup || !alreadyAttempted(readStore().records || {}, ownerAtStart, item.identity)));
                if (pending.length > cfg.maxRecipients) throw stopError("本次匹配 " + pending.length + " 位，超过单次上限 " + cfg.maxRecipients + "；未发送，请在名单中排除部分或调整上限");
                if (!pending.length) { status("没有待发送好友：可能已处理、未勾选或已不在当前列表。"); return; }
                let clicked = 0, skipped = 0;
                for (let i = 0; i < pending.length; i++) {
                    checkRun(); status("处理 " + (i + 1) + "/" + pending.length + "：" + pending[i].name);
                    try { (await sendTarget(pending[i], cfg)) === "clicked" ? clicked++ : skipped++; }
                    catch (error) { if (error.stop) throw error; skipped++; log("跳过 " + pending[i].name + "：" + error.message); }
                    if (i < pending.length - 1) await pause(cfg.intervalSeconds * 1000);
                }
                status("完成：点击发送 " + clicked + " 位，跳过 " + skipped + " 位。送达与火花状态请以抖音为准。");
            });
        } catch (error) { status(error.message); log(error.message); }
        finally { setBusy(false); ownerAtStart = ""; }
    }

    window.__SparkDesktop = Object.freeze({
        start(mode, config, omit, selected) {
            if (busy) return false;
            if (mode !== "scan" && mode !== "send") return false;
            requestedConfig = config;
            excluded = new Set(Array.isArray(omit) ? omit : []);
            selectedIdentities = new Set(Array.isArray(selected) ? selected : []);
            run(mode);
            return true;
        },
        loadEmoji(kind) { if(busy)return false;loadEmoji(kind);return true; },
        stop() { cancelled = true; },
        ack(id, success, error) {
            const item = pending.get(String(id));
            if (!item) return;
            pending.delete(String(id)); clearTimeout(item.timer);
            if (success) item.resolve(); else item.reject(stopError(error || "本地保存失败，任务已停止"));
        }
    });
    document.addEventListener("keydown", event => { if (busy && event.key === "Escape") cancelled = true; }, true);
    for (const type of ["pointerdown", "keydown", "wheel"]) document.addEventListener(type, event => {
        if (busy && event.isTrusted) { cancelled = true; status("检测到手动操作登录页面，正在停止…"); }
    }, {capture:true,passive:true});
    post("ready", {version:"1.0.0",capabilities:{scan:true,send:true,emoji:true}});
})();
