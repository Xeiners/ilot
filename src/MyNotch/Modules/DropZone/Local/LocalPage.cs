using System.Net;
using System.Text;
using System.Text.Json;
using MyNotch.Localization;

namespace MyNotch.Modules.DropZone.Local;

/// <summary>
/// The page the phone or other computer opens from the QR code or <c>mynotch.local</c>: a live chat and drop zone
/// where texts, links, pasted images and files flow both ways in real time. Self-contained (no font, script or image
/// from the Internet: it must work on a local network without any).
/// </summary>
internal static class LocalPage
{
    public static string EventsJson(long sequence, IReadOnlyList<LocalMessage> items)
    {
        var sb = new StringBuilder();
        sb.Append("{\"seq\":");
        sb.Append(sequence);
        sb.Append(",\"items\":[");
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            AppendMessageJson(sb, items[i]);
        }

        sb.Append("]}");
        return sb.ToString();
    }

    private static void AppendMessageJson(StringBuilder sb, LocalMessage item)
    {
        string kind = item.Kind switch
        {
            LocalMessageKind.Link => "link",
            LocalMessageKind.Image => "image",
            LocalMessageKind.File => "file",
            _ => "text",
        };
        string name = item.IsFolder && item.FileName is { } folder ? folder + ".zip" : item.FileName ?? "";
        string sizeText = item.FileSize > 0 ? UploadItem.FormatSize(item.FileSize) : "";
        sb.Append('{');
        sb.Append($"\"id\":\"{Encode(item.Id)}\",");
        sb.Append($"\"seq\":{item.Sequence},");
        sb.Append($"\"fromPc\":{(item.FromPc ? "true" : "false")},");
        sb.Append($"\"kind\":\"{kind}\",");
        sb.Append($"\"time\":\"{Encode(item.TimeText)}\",");
        sb.Append($"\"text\":\"{Encode(item.Text ?? "")}\",");
        sb.Append($"\"url\":\"{Encode(item.Link?.AbsoluteUri ?? "")}\",");
        sb.Append($"\"domain\":\"{Encode(item.Domain)}\",");
        sb.Append($"\"name\":\"{Encode(name)}\",");
        sb.Append($"\"size\":{item.FileSize},");
        sb.Append($"\"sizeText\":\"{Encode(sizeText)}\",");
        sb.Append($"\"isFolder\":{(item.IsFolder ? "true" : "false")},");
        sb.Append($"\"state\":\"{Encode(item.FileState)}\"");
        sb.Append('}');
    }

    private static string Encode(string value) => JsonEncodedText.Encode(value).ToString();

    public static string Render(IReadOnlyList<SharedFile> files, IReadOnlyList<LocalMessage> messages, long sequence, string ipBase = "")
    {
        string initialJson = EventsJson(sequence, messages);
        string encodedIpBase = Encode(ipBase);
        return Loc.Page($$"""
            <!doctype html>
            <html lang="fr">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, viewport-fit=cover">
            <meta name="color-scheme" content="dark">
            <title>MyNotch · Wi-Fi local</title>
            <style>
              :root { --bg:#000; --card:#161618; --pc:#18181c; --me:#11282e; --line:#ffffff1a; --me-line:#22d3ee44; --text:#f5f5f7; --dim:#8e8e93; --accent:#22d3ee; --ok:#34c759; --bad:#ff6b6b; }
              * { box-sizing:border-box; -webkit-tap-highlight-color:transparent; }
              html, body { margin:0; height:100%; height:100dvh; overflow:hidden; background:var(--bg); color:var(--text);
                     font-family:-apple-system, system-ui, "Segoe UI", Roboto, sans-serif; font-size:15px; line-height:1.45; }
              body { display:flex; flex-direction:column; position:relative; }
              body::before { content:""; position:fixed; inset:-20% -20% auto; height:70vh; z-index:0; filter:blur(60px); opacity:.48; pointer-events:none;
                     background:radial-gradient(40% 50% at 20% 40%, #8b5cf6aa, transparent 70%),
                                radial-gradient(40% 50% at 80% 30%, #22d3ee88, transparent 70%),
                                radial-gradient(50% 40% at 50% 80%, #f472b677, transparent 70%); }
              body.drag::after { content:"Déposez pour envoyer au PC"; position:fixed; inset:12px; z-index:80; border-radius:24px;
                     border:2px dashed var(--accent); background:#000000cc; backdrop-filter:blur(12px); -webkit-backdrop-filter:blur(12px);
                     display:grid; place-items:center; font:600 18px system-ui, sans-serif; color:var(--accent); pointer-events:none; }
              .top-bar { flex:none; z-index:10; width:100%;
                     padding:max(10px, env(safe-area-inset-top)) 16px 10px;
                     border-bottom:1px solid #ffffff12; background:#09090cb8;
                     backdrop-filter:blur(22px); -webkit-backdrop-filter:blur(22px); }
              .top-inner { width:100%; max-width:640px; margin:0 auto; display:flex; align-items:center; justify-content:space-between; gap:12px; }
              .brand { display:flex; align-items:center; gap:10px; min-width:0; }
              .brand-badge { width:32px; height:32px; border-radius:10px; background:#22d3ee1c; border:1px solid #22d3ee40;
                     color:var(--accent); display:grid; place-items:center; flex:none; }
              .brand-badge svg { width:16px; height:16px; }
              h1 { font-size:16px; font-weight:650; margin:0; letter-spacing:-.015em; line-height:1.15; }
              .status-pill { display:inline-flex; align-items:center; gap:7px; padding:5px 11px; border-radius:99px;
                     background:#ffffff0d; border:1px solid #ffffff17; color:var(--dim); font-size:12px; font-weight:500; white-space:nowrap; }
              .dot { width:7px; height:7px; border-radius:50%; background:var(--ok); box-shadow:0 0 8px var(--ok); flex:none; }
              #chatWrap { flex:1; min-height:0; width:100%; overflow-y:auto; overflow-x:hidden; overscroll-behavior:contain;
                     -webkit-overflow-scrolling:touch; z-index:1; }
              #chat { width:100%; max-width:640px; min-height:100%; margin:0 auto; padding:14px 14px 10px;
                     display:flex; flex-direction:column; gap:10px; }
              .empty { color:var(--dim); text-align:center; padding:32px 18px; border:1.5px dashed #ffffff24; border-radius:20px; margin:auto 0; font-size:14px; }
              .empty b { color:var(--text); display:block; margin-bottom:4px; font-size:15px; }
              .msg { max-width:86%; padding:11px 13px; border-radius:18px; border:1px solid var(--line);
                     background:color-mix(in srgb, var(--pc) 92%, transparent); backdrop-filter:blur(20px); -webkit-backdrop-filter:blur(20px);
                     display:flex; flex-direction:column; gap:6px; animation:pop .18s cubic-bezier(.2,1.2,.4,1); word-break:break-word; }
              @keyframes pop { from { opacity:0; transform:translateY(6px) scale(.97); } }
              .msg.pc { align-self:flex-start; border-bottom-left-radius:5px; }
              .msg.me { align-self:flex-end; background:color-mix(in srgb, var(--me) 92%, transparent); border-color:var(--me-line); border-bottom-right-radius:5px; }
              .head { display:flex; align-items:center; justify-content:space-between; gap:10px; font-size:11.5px; color:var(--dim); font-variant-numeric:tabular-nums; flex-wrap:wrap; }
              .acts { display:flex; align-items:center; gap:6px; }
              .act { border:0; border-radius:11px; padding:5px 10px; background:#ffffff18; color:var(--text); font:600 11.5px system-ui, sans-serif;
                     cursor:pointer; text-decoration:none; display:inline-flex; align-items:center; gap:5px; touch-action:manipulation; }
              .act:active { transform:scale(.95); }
              .act.ok { background:#34c75933; color:var(--ok); }
              .txt { white-space:pre-wrap; word-break:break-word; font-size:14.5px; line-height:1.45; }
              .lnk { display:flex; align-items:center; gap:10px; color:var(--accent); text-decoration:none; word-break:break-all; font-size:13.5px; }
              .lnk b { color:var(--text); display:block; font-size:14px; }
              .img-wrap { border-radius:12px; overflow:hidden; background:#0a0a0c; max-height:260px; display:flex; justify-content:center; align-items:center; cursor:zoom-in; position:relative; }
              .img-wrap img { max-width:100%; max-height:260px; object-fit:contain; display:block; transition:transform .18s ease; }
              .img-wrap:active img { transform:scale(.98); }
              .file-row { display:flex; align-items:center; gap:11px; color:inherit; text-decoration:none; min-width:210px; }
              .glyph { flex:none; width:38px; height:38px; border-radius:11px; display:grid; place-items:center; background:#ffffff14; }
              .meta { flex:1; min-width:0; display:flex; flex-direction:column; }
              .meta b { font-weight:600; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; font-size:14px; }
              .meta small { color:var(--dim); font-size:12px; font-variant-numeric:tabular-nums; }
              .get { flex:none; width:34px; height:34px; border-radius:50%; display:grid; place-items:center; background:var(--text); color:#000; }
              svg { width:18px; height:18px; }
              input[type=file] { display:none; }
              .bar { height:4px; border-radius:2px; background:#ffffff26; overflow:hidden; margin-top:6px; display:none; }
              .bar i { display:block; height:100%; width:0; background:var(--text); border-radius:2px; transition:width .15s linear; }
              .busy .bar, .done .bar, .failed .bar, .waiting .bar { display:block; }
              .busy .get { opacity:.35; }
              .done .bar i { background:var(--ok); }
              .failed .bar i { background:var(--bad); width:100%; }
              .done .meta small { color:var(--ok); }
              .failed .meta small { color:var(--bad); }
              .waiting .meta small { color:#ffc857; }
              .waiting .bar i { background:#ffc857; width:100%; animation:wait 1.4s ease-in-out infinite; }
              @keyframes wait { 50% { opacity:.35; } }
              .bottom-dock { flex:none; z-index:20; width:100%; background:linear-gradient(to top, #000 82%, #000000cc);
                     border-top:1px solid #ffffff14; backdrop-filter:blur(20px); -webkit-backdrop-filter:blur(20px);
                     padding:8px 12px max(10px, env(safe-area-inset-bottom)); }
              .dock-inner { max-width:640px; margin:0 auto; display:flex; flex-direction:column; gap:8px; }
              .quick { display:flex; gap:7px; }
              .qbtn { flex:1; min-width:0; display:flex; align-items:center; justify-content:center; gap:6px; padding:9px 10px;
                     border-radius:14px; background:#18181ce6; border:1px solid var(--line);
                     color:var(--text); font:600 13px system-ui, sans-serif; cursor:pointer; white-space:nowrap;
                     touch-action:manipulation; user-select:none; transition:transform .14s ease, border-color .15s; }
              .qbtn:active { transform:scale(.96); }
              .qbtn:hover { border-color:#ffffff38; }
              .qbtn.primary { background:var(--text); color:#000; border-color:transparent; }
              .qbtn svg { width:16px; height:16px; flex:none; }
              .composer { display:flex; align-items:center; gap:8px; min-height:46px; padding:6px 6px 6px 16px;
                     border-radius:23px; background:#18181cf5; border:1px solid #ffffff26; }
              .composer:focus-within { border-color:var(--accent); }
              textarea { flex:1; border:0; background:transparent; color:var(--text);
                     font-family:-apple-system, system-ui, "Segoe UI", Roboto, sans-serif;
                     font-size:16px; line-height:22px; height:22px; max-height:108px;
                     padding:0; margin:0; resize:none; outline:none; display:block; }
              textarea::placeholder { color:var(--dim); }
              .cbtn { flex:none; width:34px; height:34px; border-radius:50%; border:0; display:grid; place-items:center;
                     background:var(--accent); color:#000; cursor:pointer; padding:0; touch-action:manipulation; }
              .cbtn:active { transform:scale(.92); }
              .cbtn svg { width:17px; height:17px; }
              #toast { position:fixed; top:max(14px, env(safe-area-inset-top)); left:50%; transform:translateX(-50%) translateY(-20px);
                     background:#222228; color:var(--text); border:1px solid #ffffff28; padding:8px 16px; border-radius:99px;
                     font:600 13px system-ui, sans-serif; opacity:0; pointer-events:none; transition:opacity .18s, transform .18s; z-index:120;
                     box-shadow:0 8px 24px #00000080; white-space:nowrap; }
              #toast.on { opacity:1; transform:translateX(-50%) translateY(0); }
              #lightbox { position:fixed; inset:0; z-index:100; background:#000000eb; backdrop-filter:blur(18px); -webkit-backdrop-filter:blur(18px);
                     display:none; flex-direction:column; justify-content:space-between;
                     padding:max(14px, env(safe-area-inset-top)) 16px max(16px, env(safe-area-inset-bottom)); }
              #lightbox.open { display:flex; animation:fadeIn .16s ease-out; }
              @keyframes fadeIn { from { opacity:0; } }
              .lb-top { display:flex; align-items:center; justify-content:space-between; gap:12px; max-width:960px; width:100%; margin:0 auto; }
              .lb-title { font-size:13.5px; color:var(--dim); white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
              .lb-actions { display:flex; align-items:center; gap:8px; flex:none; }
              .lb-btn { border:1px solid #ffffff26; border-radius:99px; padding:7px 14px; background:#1c1c22; color:var(--text);
                     font:600 13px system-ui, sans-serif; cursor:pointer; text-decoration:none; display:inline-flex; align-items:center; gap:6px; }
              .lb-btn.close { width:36px; height:36px; padding:0; justify-content:center; font-size:16px; }
              .lb-body { flex:1; min-height:0; display:grid; place-items:center; padding:12px 0; }
              .lb-body img { max-width:95vw; max-height:80dvh; object-fit:contain; border-radius:12px; box-shadow:0 18px 50px #000; }
              @media (max-width: 480px) {
                .msg { max-width:92%; padding:10px 12px; }
                .file-row { min-width:0; }
                .qbtn { padding:9px 6px; font-size:12.5px; gap:5px; }
              }
            </style>
            </head>
            <body>
            <div id="toast"></div>

            <div id="lightbox">
              <div class="lb-top">
                <span class="lb-title" id="lbTitle"></span>
                <div class="lb-actions">
                  <a class="lb-btn" id="lbDl" download>{{DownloadIcon}}<span>Télécharger</span></a>
                  <button type="button" class="lb-btn close" id="lbClose" aria-label="Fermer">✕</button>
                </div>
              </div>
              <div class="lb-body" id="lbBody">
                <img id="lbImg" alt="Aperçu">
              </div>
            </div>

            <header class="top-bar">
              <div class="top-inner">
                <div class="brand">
                  <span class="brand-badge">{{WifiIcon}}</span>
                  <h1>MyNotch</h1>
                </div>
                <span class="status-pill"><span class="dot"></span>Wi-Fi local</span>
              </div>
            </header>

            <main id="chatWrap">
              <div id="chat">
                <div class="empty" id="emptyState">
                  <b>Chat et partage prêts</b>
                  Écrivez un message, collez une image ou un lien, ou envoyez des fichiers : tout s'affiche en direct dans la notch.
                </div>
              </div>
            </main>

            <footer class="bottom-dock">
              <div class="dock-inner">
                <div class="quick">
                  <button type="button" class="qbtn primary" id="pasteBtn">{{PasteIcon}}<span>Coller</span></button>
                  <label class="qbtn">{{ImageIcon}}<span>Photo / Image</span><input id="pickImg" type="file" accept="image/*" multiple></label>
                  <label class="qbtn">{{UploadIcon}}<span>Fichiers</span><input id="pickFile" type="file" multiple></label>
                </div>
                <form class="composer" id="form">
                  <textarea id="msgInput" rows="1" placeholder="Message ou lien…"></textarea>
                  <button type="submit" class="cbtn" title="Envoyer">{{SendArrowIcon}}</button>
                </form>
              </div>
            </footer>

            <script>
              const chatWrap = document.getElementById('chatWrap'), chat = document.getElementById('chat'), emptyState = document.getElementById('emptyState');
              const msgInput = document.getElementById('msgInput'), form = document.getElementById('form');
              const pasteBtn = document.getElementById('pasteBtn'), pickImg = document.getElementById('pickImg'), pickFile = document.getElementById('pickFile');
              const toastEl = document.getElementById('toast');
              const lightbox = document.getElementById('lightbox'), lbImg = document.getElementById('lbImg'), lbTitle = document.getElementById('lbTitle'), lbDl = document.getElementById('lbDl'), lbClose = document.getElementById('lbClose'), lbBody = document.getElementById('lbBody');
              const FileSvg = '{{FileIcon}}', FolderSvg = '{{FolderIcon}}', DownloadSvg = '{{DownloadIcon}}', LinkSvg = '{{LinkIcon}}';
              const ipBase = "{{encodedIpBase}}";
              let activeBase = '';

              function apiUrl(path, useFallback = false) {
                const base = useFallback && ipBase ? ipBase : activeBase;
                return base ? base + path : path;
              }

              function scrollToBottom(smooth = true) {
                chatWrap.scrollTo({ top: chatWrap.scrollHeight, behavior: smooth ? 'smooth' : 'auto' });
              }

              function openPreview(src, dlHref, name, sizeText) {
                lbImg.src = src;
                lbTitle.textContent = (name || 'Image') + (sizeText ? ' · ' + sizeText : '');
                lbDl.href = dlHref;
                lbDl.setAttribute('download', name || 'image.png');
                lightbox.classList.add('open');
              }

              function closePreview() {
                lightbox.classList.remove('open');
                lbImg.removeAttribute('src');
              }

              lbClose.onclick = closePreview;
              lbBody.onclick = e => { if (e.target === lbBody) closePreview(); };
              window.addEventListener('keydown', e => { if (e.key === 'Escape' && lightbox.classList.contains('open')) closePreview(); });

              const size = b => b < 1024 * 1024 ? Math.max(1, Math.round(b / 1024)) + ' Ko'
                : b < 1024 * 1024 * 1024 ? (b / 1048576).toFixed(b < 10485760 ? 1 : 0).replace('.', ',') + ' Mo'
                : (b / 1073741824).toFixed(1).replace('.', ',') + ' Go';
              const lasted = s => s < 1 ? 'moins d’1 s' : s < 60 ? Math.round(s) + ' s' : Math.floor(s / 60) + ' min ' + Math.round(s % 60) + ' s';

              let toastTimer = 0;
              function toast(text) {
                toastEl.textContent = text;
                toastEl.classList.add('on');
                clearTimeout(toastTimer);
                toastTimer = setTimeout(() => toastEl.classList.remove('on'), 1800);
              }

              async function copyText(text, btn) {
                let ok = false;
                if (navigator.clipboard && window.isSecureContext) {
                  try { await navigator.clipboard.writeText(text); ok = true; } catch {}
                }
                if (!ok) {
                  const area = document.createElement('textarea');
                  area.value = text;
                  area.style.position = 'fixed';
                  area.style.opacity = '0';
                  document.body.append(area);
                  area.select();
                  try { ok = document.execCommand('copy'); } catch {}
                  area.remove();
                }
                if (btn) {
                  const prev = btn.textContent;
                  btn.textContent = 'Copié';
                  btn.classList.add('ok');
                  setTimeout(() => { btn.textContent = prev; btn.classList.remove('ok'); }, 1500);
                } else {
                  toast('Copié dans le presse-papier');
                }
              }

              function meter() {
                const start = performance.now();
                let at = start, seen = 0, rate = 0;
                return {
                  tick(bytes) {
                    const now = performance.now(), dt = (now - at) / 1000;
                    if (dt >= 0.3) {
                      const r = (bytes - seen) / dt;
                      rate = rate ? rate * 0.6 + r * 0.4 : r;
                      at = now;
                      seen = bytes;
                    }
                    return rate ? ' · ' + size(rate) + '/s' : '';
                  },
                  summary(bytes) {
                    const s = (performance.now() - start) / 1000;
                    return size(bytes) + ' en ' + lasted(s) + (s > 0.05 ? ' · ' + size(bytes / s) + '/s' : '');
                  },
                };
              }

              const Inline = 512 * 1024 * 1024;
              async function fetchFile(link, total) {
                const card = link.closest('.msg'), bar = card.querySelector('.bar i'), note = card.querySelector('.meta small');
                card.classList.remove('done', 'failed');
                card.classList.add('busy');
                bar.style.width = '0';
                note.textContent = 'Connexion…';
                try {
                  const response = await fetch(link.href, { cache: 'no-store' });
                  if (!response.ok || !response.body) throw new Error();
                  const reader = response.body.getReader(), parts = [], speed = meter();
                  let got = 0;
                  for (;;) {
                    const { done, value } = await reader.read();
                    if (done) break;
                    parts.push(value);
                    got += value.length;
                    const p = total ? Math.min(1, got / total) : 0;
                    bar.style.width = (p * 100) + '%';
                    note.textContent = Math.round(p * 100) + ' %' + speed.tick(got);
                  }
                  // Its type as the PC gives it: without one, Chrome on Android saved a PDF as « .pdf.txt ».
                  const type = response.headers.get('Content-Type') || 'application/octet-stream';
                  const url = URL.createObjectURL(new Blob(parts, { type }));
                  const save = document.createElement('a');
                  save.href = url;
                  save.download = link.getAttribute('download') || 'fichier';
                  document.body.append(save);
                  save.click();
                  save.remove();
                  setTimeout(() => URL.revokeObjectURL(url), 60000);
                  card.classList.add('done');
                  note.textContent = 'Téléchargé · ' + speed.summary(got);
                } catch {
                  card.classList.add('failed');
                  note.textContent = 'Échec, touchez pour réessayer';
                } finally {
                  card.classList.remove('busy');
                  bar.style.width = '100%';
                }
              }

              function upsert(item, scroll = true) {
                emptyState.style.display = 'none';
                let el = document.querySelector(`.msg[data-id="${item.id}"]`);
                const isNew = !el;
                if (isNew) {
                  el = document.createElement('div');
                  el.dataset.id = item.id;
                  chat.append(el);
                }
                el.className = 'msg ' + (item.fromPc ? 'pc' : 'me');
                const who = (item.fromPc ? 'Depuis le PC' : 'Cet appareil') + (item.time ? ' · ' + item.time : '');

                if (item.kind === 'text') {
                  el.innerHTML = '<div class="head"><span></span><div class="acts"><button type="button" class="act">Copier</button></div></div><div class="txt"></div>';
                  el.querySelector('.head span').textContent = who;
                  el.querySelector('.txt').textContent = item.text;
                  const btn = el.querySelector('.act');
                  btn.onclick = () => copyText(item.text, btn);
                } else if (item.kind === 'link') {
                  el.innerHTML = '<div class="head"><span></span><div class="acts"><button type="button" class="act">Copier</button><a class="act" target="_blank" rel="noopener">Ouvrir</a></div></div><a class="lnk" target="_blank" rel="noopener"><span class="glyph">' + LinkSvg + '</span><span class="meta"><b></b><small></small></span></a>';
                  el.querySelector('.head span').textContent = who;
                  const btn = el.querySelector('button.act'), openBtn = el.querySelector('a.act'), lnk = el.querySelector('a.lnk');
                  btn.onclick = () => copyText(item.url || item.text, btn);
                  openBtn.href = lnk.href = item.url || item.text;
                  lnk.querySelector('b').textContent = item.domain || item.url;
                  lnk.querySelector('small').textContent = item.url || item.text;
                } else if (item.kind === 'image') {
                  const mediaUrl = apiUrl('media/' + encodeURIComponent(item.id));
                  const dlUrl = mediaUrl + '?download=1';
                  el.innerHTML = '<div class="head"><span></span><div class="acts"><button type="button" class="act">Aperçu</button><a class="act" download>Télécharger</a></div></div><div class="img-wrap"><img loading="lazy"></div>';
                  el.querySelector('.head span').textContent = who + (item.sizeText ? ' · ' + item.sizeText : '');
                  const viewBtn = el.querySelector('button.act'), dl = el.querySelector('a.act'), wrap = el.querySelector('.img-wrap'), img = el.querySelector('img');
                  dl.href = dlUrl;
                  dl.setAttribute('download', item.name || 'image.png');
                  img.src = mediaUrl;
                  img.onerror = () => {
                    if (ipBase && !img.dataset.retried) {
                      img.dataset.retried = '1';
                      activeBase = ipBase;
                      img.src = apiUrl('media/' + encodeURIComponent(item.id));
                      dl.href = img.src + '?download=1';
                    }
                  };
                  img.alt = item.name || 'Image';
                  const showLb = () => openPreview(img.src, dl.href, item.name || 'image.png', item.sizeText);
                  wrap.onclick = showLb;
                  viewBtn.onclick = showLb;
                } else {
                  const dlUrl = apiUrl('media/' + encodeURIComponent(item.id) + '?download=1');
                  const kindPrefix = item.isFolder ? 'Dossier · ' : '';
                  if (item.fromPc) {
                    el.innerHTML = '<div class="head"><span></span></div><a class="file-row" href="' + dlUrl + '"><span class="glyph">' + (item.isFolder ? FolderSvg : FileSvg) + '</span><span class="meta"><b></b><small></small><span class="bar"><i></i></span></span><span class="get">' + DownloadSvg + '</span></a>';
                    el.querySelector('.head span').textContent = who;
                    const a = el.querySelector('a.file-row');
                    a.setAttribute('download', item.name);
                    a.querySelector('b').textContent = item.name;
                    a.querySelector('small').textContent = kindPrefix + item.sizeText;
                    a.onclick = e => {
                      if (el.classList.contains('busy')) { e.preventDefault(); return; }
                      if (!window.ReadableStream || !window.fetch || item.size > Inline || item.isFolder) return;
                      e.preventDefault();
                      fetchFile(a, item.size);
                    };
                  } else {
                    el.classList.toggle('waiting', item.state === 'pending');
                    el.classList.toggle('done', item.state === 'accepted');
                    el.classList.toggle('failed', item.state === 'refused');
                    const status = item.state === 'accepted' ? 'Accepté par le PC · ' + item.sizeText
                      : item.state === 'refused' ? 'Refusé par le PC'
                      : 'En attente de l’accord du PC · ' + item.sizeText;
                    el.innerHTML = '<div class="head"><span></span></div><div class="file-row"><span class="glyph">' + FileSvg + '</span><span class="meta"><b></b><small></small><span class="bar"><i></i></span></span></div>';
                    el.querySelector('.head span').textContent = who;
                    el.querySelector('b').textContent = item.name;
                    el.querySelector('small').textContent = status;
                  }
                }

                if (scroll && isNew) {
                  scrollToBottom(true);
                }
              }

              async function sendText(text) {
                const trimmed = (text || '').trim();
                if (!trimmed) return;
                try {
                  const res = await fetch(apiUrl('chat/text'), { method: 'POST', body: trimmed });
                  if (!res.ok) throw new Error();
                } catch {
                  if (ipBase && activeBase !== ipBase) {
                    activeBase = ipBase;
                    try {
                      const retry = await fetch(apiUrl('chat/text'), { method: 'POST', body: trimmed });
                      if (retry.ok) return;
                    } catch {}
                  }
                  toast('Envoi impossible');
                }
              }

              const MaxChatImg = 100 * 1024 * 1024;
              const queue = [];
              let busy = false;
              function enqueueFile(file, asChatImage) {
                if (!file) return;
                const isImg = Boolean(asChatImage && file.type && file.type.startsWith('image/') && file.size > 0 && file.size <= MaxChatImg);
                const el = document.createElement('div');
                el.className = 'msg me busy';
                el.innerHTML = '<div class="head"><span>Cet appareil · Envoi…</span><div class="acts"><button type="button" class="act retry-btn" style="display:none">Réessayer</button></div></div><div class="file-row"><span class="glyph">' + FileSvg + '</span><span class="meta"><b></b><small></small><span class="bar"><i></i></span></span></div>';
                el.querySelector('b').textContent = file.name || 'Image collée.png';
                el.querySelector('small').textContent = 'En attente · ' + size(file.size);
                const retryBtn = el.querySelector('.retry-btn');
                retryBtn.onclick = () => {
                  retryBtn.style.display = 'none';
                  el.classList.remove('failed');
                  el.classList.add('busy');
                  el.querySelector('small').textContent = 'En attente · ' + size(file.size);
                  queue.push({ file, el, isImg, retriedIp: false });
                  nextUpload();
                };
                emptyState.style.display = 'none';
                chat.append(el);
                scrollToBottom(true);
                queue.push({ file, el, isImg, retriedIp: false });
                nextUpload();
              }

              function nextUpload() {
                if (busy || !queue.length) return;
                busy = true;
                const task = queue.shift();
                const { file, el, isImg } = task;
                const bar = el.querySelector('.bar i'), note = el.querySelector('.meta small'), retryBtn = el.querySelector('.retry-btn');
                const xhr = new XMLHttpRequest(), speed = meter();
                const route = isImg ? 'chat/image' : 'upload';
                xhr.open('POST', apiUrl(route, task.retriedIp));
                xhr.setRequestHeader('X-File-Name', encodeURIComponent(file.name || 'image.png'));
                xhr.upload.onprogress = e => {
                  if (!e.lengthComputable) return;
                  const p = e.loaded / e.total;
                  bar.style.width = (p * 100) + '%';
                  note.textContent = Math.round(p * 100) + ' %' + speed.tick(e.loaded);
                };
                const end = (ok, id) => {
                  if (!ok && !task.retriedIp && ipBase && activeBase !== ipBase) {
                    activeBase = ipBase;
                    task.retriedIp = true;
                    busy = false;
                    queue.unshift(task);
                    nextUpload();
                    return;
                  }
                  busy = false;
                  nextUpload();
                  if (!ok) {
                    el.classList.remove('busy');
                    el.classList.add('failed');
                    note.textContent = 'Échec de l’envoi';
                    if (retryBtn) retryBtn.style.display = '';
                    return;
                  }
                  if (id) {
                    const existing = document.querySelector(`.msg[data-id="${id}"]`);
                    if (existing && existing !== el) {
                      el.remove();
                      return;
                    }
                    el.dataset.id = id;
                  }
                  if (!isImg) {
                    el.classList.remove('busy');
                    el.classList.add('waiting');
                    note.textContent = 'Envoyé · en attente de l’accord du PC…';
                  }
                };
                xhr.onload = () => {
                  let reply = null;
                  try { reply = JSON.parse(xhr.responseText); } catch {}
                  end(xhr.status === 200 && reply && reply.ok, reply && reply.id);
                };
                xhr.onerror = xhr.onabort = () => end(false);
                xhr.send(file);
              }

              function fitInput() {
                msgInput.style.height = '22px';
                const sh = msgInput.scrollHeight;
                if (sh > 24) {
                  msgInput.style.height = Math.min(sh, 108) + 'px';
                }
              }

              msgInput.addEventListener('input', fitInput);
              msgInput.addEventListener('keydown', e => {
                if (e.key === 'Enter' && !e.shiftKey) {
                  e.preventDefault();
                  form.requestSubmit();
                }
              });
              form.addEventListener('submit', e => {
                e.preventDefault();
                const val = msgInput.value;
                if (!val.trim()) return;
                msgInput.value = '';
                fitInput();
                sendText(val);
              });

              pickImg.onclick = () => { pickImg.value = ''; };
              pickFile.onclick = () => { pickFile.value = ''; };
              pickImg.onchange = () => { for (const f of Array.from(pickImg.files || [])) enqueueFile(f, true); };
              pickFile.onchange = () => { for (const f of Array.from(pickFile.files || [])) enqueueFile(f, false); };

              window.addEventListener('paste', e => {
                const items = e.clipboardData && e.clipboardData.items;
                if (!items) return;
                let handledFile = false;
                for (const item of items) {
                  if (item.kind === 'file') {
                    const f = item.getAsFile();
                    if (f) {
                      enqueueFile(f, f.type && f.type.startsWith('image/'));
                      handledFile = true;
                    }
                  }
                }
                if (handledFile) {
                  e.preventDefault();
                  return;
                }
                if (document.activeElement !== msgInput) {
                  const text = e.clipboardData.getData('text/plain');
                  if (text && text.trim()) {
                    e.preventDefault();
                    sendText(text);
                    toast('Texte collé et envoyé');
                  }
                }
              });

              pasteBtn.onclick = async () => {
                if (navigator.clipboard && navigator.clipboard.read) {
                  try {
                    const items = await navigator.clipboard.read();
                    for (const item of items) {
                      const imgType = item.types.find(t => t.startsWith('image/'));
                      if (imgType) {
                        const blob = await item.getType(imgType);
                        const ext = imgType.split('/')[1] || 'png';
                        enqueueFile(new File([blob], 'Presse-papier.' + ext, { type: imgType }), true);
                        return;
                      }
                    }
                  } catch {}
                }
                if (navigator.clipboard && navigator.clipboard.readText) {
                  try {
                    const text = await navigator.clipboard.readText();
                    if (text && text.trim()) {
                      sendText(text);
                      toast('Collé et envoyé');
                      return;
                    }
                  } catch {}
                }
                msgInput.focus();
                toast('Maintenez appuyé dans le champ (ou Ctrl+V) pour coller');
              };

              let dragDepth = 0;
              window.addEventListener('dragenter', e => { e.preventDefault(); dragDepth++; document.body.classList.add('drag'); });
              window.addEventListener('dragover', e => e.preventDefault());
              window.addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; document.body.classList.remove('drag'); } });
              window.addEventListener('drop', e => {
                e.preventDefault();
                dragDepth = 0;
                document.body.classList.remove('drag');
                if (e.dataTransfer && e.dataTransfer.files) {
                  for (const f of Array.from(e.dataTransfer.files)) {
                    enqueueFile(f, f.type && f.type.startsWith('image/'));
                  }
                }
              });

              const initial = {{initialJson}};
              let seq = initial.seq || 0;
              for (const item of initial.items || []) upsert(item, false);
              if (initial.items && initial.items.length) {
                scrollToBottom(false);
              }

              async function poll() {
                for (;;) {
                  try {
                    const res = await fetch(apiUrl('chat/events?since=' + seq), { cache: 'no-store' });
                    if (!res.ok) throw new Error();
                    const data = await res.json();
                    if (typeof data.seq === 'number') seq = data.seq;
                    for (const item of data.items || []) upsert(item, true);
                  } catch {
                    if (ipBase && activeBase !== ipBase) {
                      activeBase = ipBase;
                    }
                    await new Promise(r => setTimeout(r, 1500));
                  }
                }
              }
              poll();
            </script>
            </body>
            </html>
            """);
    }

    /// <summary>
    /// For any address without the session's key (the address typed by hand, an old QR code…): asks for the code
    /// the notch shows, with <paramref name="message"/> above it if there is something to say.
    /// </summary>
    public static string Code(string? message) => Loc.Page($$"""
        <!doctype html>
        <html lang="fr">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1">
        <meta name="color-scheme" content="dark">
        <title>MyNotch · Wi-Fi local</title>
        <style>
          * { box-sizing:border-box; }
          body { margin:0; min-height:100vh; min-height:100dvh; display:grid; place-items:center; background:#000; color:#f5f5f7; padding:24px;
                 font:15px/1.4 -apple-system, system-ui, "Segoe UI", Roboto, sans-serif; text-align:center; }
          main { width:100%; max-width:340px; }
          .pill { width:46px; height:22px; border-radius:11px; margin:0 auto 18px; box-shadow:0 0 0 1px #ffffff26, 0 6px 20px #22d3ee40; }
          h1 { font-size:20px; margin:0 0 6px; }
          p { color:#8e8e93; margin:0 0 22px; }
          .message { color:#ff6b6b; }
          input { width:100%; font:600 34px/1 ui-monospace, "SF Mono", Menlo, monospace; letter-spacing:.4em; text-align:center;
                  padding:14px 0 14px .4em; border-radius:18px; border:1px solid #ffffff26; background:#161618; color:inherit; outline:none; }
          input:focus { border-color:#22d3ee; }
          button { width:100%; margin-top:12px; padding:15px; border:0; border-radius:18px; background:#f5f5f7; color:#000; font:600 16px system-ui, sans-serif; cursor:pointer; }
        </style>
        </head>
        <body>
        <main>
          <div class="pill"></div>
          <h1>Code de la notch</h1>
          <p class="{{(message is null ? "" : "message")}}">{{WebUtility.HtmlEncode(message ?? Loc.T("Le code à 4 chiffres affiché sur le PC, à côté du QR code."))}}</p>
          <form method="get" action="/">
            <input name="code" inputmode="numeric" pattern="[0-9]*" maxlength="4" autocomplete="off" autofocus required>
            <button>Ouvrir</button>
          </form>
        </main>
        </body>
        </html>
        """);

    private const string FileIcon = """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/></svg>""";

    private const string FolderIcon = """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></svg>""";

    private const string DownloadIcon = """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 4v12M6 11l6 6 6-6M5 20h14"/></svg>""";

    private const string UploadIcon = """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round"><path d="M12 20V8M6 13l6-6 6 6M5 4h14"/></svg>""";

    private const string PasteIcon = """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2"/><rect x="8" y="2" width="8" height="4" rx="1"/></svg>""";

    private const string ImageIcon = """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="3"/><circle cx="8.5" cy="8.5" r="1.5"/><path d="M21 15l-5-5L5 21"/></svg>""";

    private const string LinkIcon = """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/></svg>""";

    private const string SendArrowIcon = """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19V5M5 12l7-7 7 7"/></svg>""";

    private const string WifiIcon = """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12.55a11 11 0 0 1 14.08 0"/><path d="M1.42 9a16 16 0 0 1 21.16 0"/><path d="M8.53 16.11a6 6 0 0 1 6.95 0"/><circle cx="12" cy="20" r="1" fill="currentColor"/></svg>""";
}
