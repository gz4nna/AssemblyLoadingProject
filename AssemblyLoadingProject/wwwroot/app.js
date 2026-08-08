/**
 * 前端通用工具（app.js）
 * 提供所有纯 HTML 页面共用的 AJAX 请求封装、状态文本/颜色映射、
 * HTML 转义与时间格式化工具。
 * 依赖：无。仅使用浏览器原生 fetch 与标准 JS。
 */

/**
 * 通用 AJAX 请求封装。
 * - 非 2xx 响应会解析响应体中的 message（若有）并抛出 Error；
 * - 响应为 JSON 时解析为对象，否则原样返回文本。
 * @param {string} url        请求地址
 * @param {object} [options]  fetch 选项（method/headers/body 等）
 * @returns {Promise<object|string>} 解析后的响应体
 */
async function api(url, options) {
    const res = await fetch(url, options);
    if (!res.ok) {
        // 尽量从后端错误响应中提取可读信息，否则回退到 HTTP 状态码
        let msg = 'HTTP ' + res.status;
        try {
            const j = await res.json();
            if (j && j.message) msg = j.message;
        } catch (e) {
            // 响应体不是合法 JSON，忽略
        }
        throw new Error(msg);
    }
    const ct = res.headers.get('content-type') || '';
    if (ct.includes('application/json')) return res.json();
    return res.text();
}

/** GET 请求（无请求体）。 */
function apiGet(url) {
    return api(url);
}

/** POST 请求，body 自动序列化为 JSON（未传 body 时不设置）。 */
function apiPost(url, body) {
    return api(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body)
    });
}

/**
 * 将后端插件状态枚举映射为中文文本，便于直接展示。
 * @param {string} s 后端 PluginStatus 枚举值
 * @returns {string} 中文状态
 */
function statusText(s) {
    switch (s) {
        case 'Discovered': return '已发现';   // 已扫描到 DLL，尚未加载
        case 'Loaded': return '已加载';       // 已加载，等待调度
        case 'Running': return '运行中';      // 正在执行
        case 'Stopped': return '已停止';      // 已停止调度
        case 'Faulted': return '异常';        // 加载或执行失败
        case 'Unloaded': return '已卸载';     // 已从内存卸载
        default: return s;
    }
}

/**
 * 状态 → 颜色（便于在界面中突出异常/运行态）。
 * @param {string} s 后端 PluginStatus 枚举值
 * @returns {string} CSS 颜色值
 */
function statusColor(s) {
    switch (s) {
        case 'Running': return '#3b82f6';     // 运行中 - 蓝色
        case 'Loaded': return '#16a34a';      // 已加载 - 绿色
        case 'Faulted': return '#dc2626';     // 异常 - 红色
        case 'Discovered': return '#0ea5e9';  // 已发现 - 天蓝
        default: return '#6b7280';            // 其它 - 灰
    }
}

/**
 * HTML 转义，防止后端返回的文本被当作标签执行（XSS 防护）。
 * @param {*} s 任意值，null/undefined 视为空串
 * @returns {string} 转义后的安全文本
 */
function esc(s) {
    return String(s == null ? '' : s)
        .replace(/&/g, '&amp;').replace(/</g, '&lt;')
        .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

/**
 * 时间格式化：ISO 字符串 → "MM-dd HH:mm:ss"。
 * 空值或非法日期返回占位符 '—'。
 * @param {string|null} dt ISO 8601 时间字符串
 * @returns {string} 格式化后的本地时间
 */
function fmtTime(dt) {
    if (!dt) return '—';
    const d = new Date(dt);
    if (isNaN(d)) return '—';
    const p = n => String(n).padStart(2, '0');
    return `${p(d.getMonth()+1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}
