// 通用 AJAX 帮助函数
async function api(url, options) {
    const res = await fetch(url, options);
    if (!res.ok) {
        let msg = 'HTTP ' + res.status;
        try { const j = await res.json(); if (j && j.message) msg = j.message; } catch (e) {}
        throw new Error(msg);
    }
    const ct = res.headers.get('content-type') || '';
    if (ct.includes('application/json')) return res.json();
    return res.text();
}

function apiGet(url) {
    return api(url);
}

function apiPost(url, body) {
    return api(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body)
    });
}

// 状态文本/颜色映射
function statusText(s) {
    switch (s) {
        case 'Discovered': return '已发现';
        case 'Loaded': return '已加载';
        case 'Running': return '运行中';
        case 'Stopped': return '已停止';
        case 'Faulted': return '异常';
        case 'Unloaded': return '已卸载';
        default: return s;
    }
}

function statusColor(s) {
    switch (s) {
        case 'Running': return '#3b82f6';
        case 'Loaded': return '#16a34a';
        case 'Faulted': return '#dc2626';
        case 'Discovered': return '#0ea5e9';
        default: return '#6b7280';
    }
}

function esc(s) {
    return String(s == null ? '' : s)
        .replace(/&/g, '&amp;').replace(/</g, '&lt;')
        .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

function fmtTime(dt) {
    if (!dt) return '—';
    const d = new Date(dt);
    if (isNaN(d)) return '—';
    const p = n => String(n).padStart(2, '0');
    return `${p(d.getMonth()+1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}
