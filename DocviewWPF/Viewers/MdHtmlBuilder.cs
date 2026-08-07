using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace DocviewWPF;

/// <summary>
/// MdDoc → HTML 预览（WebView2）。块元素带 data-line（源行 0-based），供滚动同步 / 目录跳转。
/// 本地图用虚拟主机前缀（默认 https://md.assets/），由 WebView2 SetVirtualHostNameToFolderMapping 映射。
/// </summary>
static class MdHtmlBuilder {
	public const string AssetHost = "md.assets";
	/// <summary>内置静态资源（mermaid / highlight.js 等），映射到 exe 旁 Assets\。</summary>
	public const string StaticHost = "md.static";
	const string MermaidCdn = "https://cdn.jsdelivr.net/npm/mermaid@11.4.1/dist/mermaid.min.js";
	// CDN 仅核心库（无语言包）；本地 Assets/highlight.min.js 已捆绑常用语言
	const string HighlightCdn = "https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.11.1/highlight.min.js";
	const string HighlightCssCdn = "https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.11.1/styles/github.min.css";
	/// <summary>预览代码块超过此行数时默认收起，显示「显示全部」。</summary>
	const int CodeFoldLines = 15;

	/// <param name="baseFilePath">当前 md 路径，解析相对图/链。</param>
	/// <param name="fontScale">相对 1.0 的正文字号倍率（另可用 WebView ZoomFactor）。</param>
	/// <param name="pageWidth">预览区宽度（DIP），表格列宽按 MdTableLayout / mdview 算法分配。</param>
	/// <param name="tabSize">Tab 显示列宽；列表用 Level（indent_cols）按 ch 缩进。</param>
	public static string Build(MdDoc doc, string baseFilePath = null, double fontScale = 1.0,
		double pageWidth = 720, int tabSize = 3) =>
		Build(doc, baseFilePath, fontScale, pageWidth, tabSize, out _);

	/// <param name="assetRoot">WebView 虚拟主机映射目录（覆盖文档目录及 ../ 图片）。</param>
	public static string Build(MdDoc doc, string baseFilePath, double fontScale,
		double pageWidth, int tabSize, out string assetRoot) {
		var pageW = pageWidth > 100 ? pageWidth : 720;
		if (tabSize < 1) tabSize = 1;
		if (tabSize > 8) tabSize = 8;
		assetRoot = ComputeAssetRoot(baseFilePath, doc);
		var mapRoot = assetRoot;
		var body = new StringBuilder(4096);
		var hasMermaid = false;
		var hasCodeHl = false;
		if (doc == null || doc.Blocks == null || doc.Blocks.Count == 0) {
			body.Append("<p class=\"empty\">(空文档)</p>");
		} else {
			// 有序序号：按缩进列宽分档
			var olCount = new Dictionary<int, int>();
			void resetOlDeeper(int level) {
				var kill = new List<int>();
				foreach (var kv in olCount)
					if (kv.Key > level) kill.Add(kv.Key);
				foreach (var k in kill) olCount.Remove(k);
			}
			// 每级缩进 25px（一级 = tabSize 列）
			const int indentStepPx = 25;
			var headAutoNum = true;
			try { headAutoNum = AppSettings.Current?.MdHeadingAutoNumber ?? true; } catch { /* keep true */ }
			var headNum = headAutoNum ? new MdHeadingNumber() : null;

			void appendblocks(IList<MdBlock> blocks) {
				if (blocks == null) return;
				foreach (var b in blocks) {
					if (b == null) continue;
					if (b.Kind != MdBlockKind.ListItem)
						olCount.Clear();

					switch (b.Kind) {
						case MdBlockKind.Blank:
							break;
						case MdBlockKind.Heading: {
							var lv = Math.Max(1, Math.Min(6, b.Level));
							body.Append("<h").Append(lv)
								.Append(" data-line=\"").Append(b.SourceLine0).Append("\">");
							if (headNum != null && !string.IsNullOrWhiteSpace(b.Text)) {
								body.Append("<span class=\"hnum\">")
									.Append(enc(headNum.Next(lv)))
									.Append("</span> ");
							}
							appendspans(body, b.Spans, b.Text, baseFilePath, mapRoot);
							body.Append("</h").Append(lv).Append('>');
							break;
						}
						case MdBlockKind.Paragraph: {
							if (b.Spans != null && b.Spans.Count == 1 && b.Spans[0].Kind == "image") {
								body.Append("<figure data-line=\"").Append(b.SourceLine0).Append("\">");
								appendimage(body, b.Spans[0], baseFilePath, mapRoot, block: true);
								if (!string.IsNullOrEmpty(b.Spans[0].Text))
									body.Append("<figcaption>").Append(enc(b.Spans[0].Text)).Append("</figcaption>");
								body.Append("</figure>");
								break;
							}
							body.Append("<p data-line=\"").Append(b.SourceLine0).Append("\">");
							appendspans(body, b.Spans, b.Text, baseFilePath, mapRoot);
							body.Append("</p>");
							break;
						}
						case MdBlockKind.Quote: {
							body.Append("<blockquote data-line=\"").Append(b.SourceLine0).Append("\">");
							appendspans(body, b.Spans, b.Text, baseFilePath, mapRoot);
							body.Append("</blockquote>");
							break;
						}
						case MdBlockKind.Code: {
							var lang = (b.Lang ?? "").Trim();
							if (ismermaid(lang)) {
								hasMermaid = true;
								body.Append("<div class=\"mermaidwrap\" data-line=\"").Append(b.SourceLine0).Append("\">");
								body.Append("<pre class=\"mermaid\">");
								body.Append(enc(b.Text ?? ""));
								body.Append("</pre></div>");
								break;
							}
							hasCodeHl = true;
							var codeText = b.Text ?? "";
							var collapsible = countcodelines(codeText) > CodeFoldLines;
							body.Append("<div class=\"codewrap");
							if (collapsible) body.Append(" collapsible");
							body.Append("\" data-line=\"").Append(b.SourceLine0).Append("\">");
							body.Append("<div class=\"codehead\"><span>")
								.Append(enc(string.IsNullOrEmpty(lang) ? "code" : lang))
								.Append("</span>");
							if (collapsible) {
								var showAll = Loc.T("code_show_all");
								var collapse = Loc.T("code_collapse");
								body.Append("<button type=\"button\" class=\"codefold\" data-expand=\"")
									.Append(enc(showAll))
									.Append("\" data-collapse=\"")
									.Append(enc(collapse))
									.Append("\">")
									.Append(enc(showAll))
									.Append("</button>");
							}
							body.Append("</div>");
							body.Append("<pre class=\"codebody\"><code");
							if (!string.IsNullOrEmpty(lang))
								body.Append(" class=\"language-").Append(enc(lang.ToLowerInvariant())).Append('"');
							body.Append('>');
							body.Append(enc(codeText));
							body.Append("</code></pre></div>");
							break;
						}
						case MdBlockKind.Hr:
							body.Append("<hr data-line=\"").Append(b.SourceLine0).Append("\"/>");
							break;
						case MdBlockKind.ListItem: {
							var cols = Math.Max(0, b.Level);
							resetOlDeeper(cols);
							string mark;
							if (b.Ordered) {
								if (!olCount.TryGetValue(cols, out var n)) n = 0;
								n++;
								olCount[cols] = n;
								mark = n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
							} else {
								mark = "•";
							}
							var pad = (int)Math.Round(cols * (double)indentStepPx / tabSize);
							body.Append("<div class=\"mdli\" data-line=\"").Append(b.SourceLine0)
								.Append("\" style=\"padding-left:")
								.Append(pad.ToString(System.Globalization.CultureInfo.InvariantCulture))
								.Append("px\">");
							body.Append("<span class=\"mdmark\">").Append(enc(mark)).Append("</span>");
							if (b.TaskChecked.HasValue) {
								body.Append("<span class=\"mdcb")
									.Append(b.TaskChecked.Value ? " on" : "")
									.Append("\" aria-hidden=\"true\"></span>");
							}
							appendspans(body, b.Spans, b.Text, baseFilePath, mapRoot);
							body.Append("</div>");
							break;
						}
						case MdBlockKind.Table: {
							appendtable(body, b, pageW, baseFilePath, mapRoot);
							break;
						}
						case MdBlockKind.HtmlImg: {
							body.Append("<figure class=\"htmlimg\" data-line=\"").Append(b.SourceLine0).Append("\">");
							var sp = (b.Spans != null && b.Spans.Count > 0)
								? b.Spans[0]
								: new MdSpan { Kind = "image", Text = "", Href = b.Text };
							appendimage(body, sp, baseFilePath, mapRoot, block: true, b.ImgWidthPx, b.ImgHeightPx);
							if (!string.IsNullOrEmpty(sp.Text))
								body.Append("<figcaption>").Append(enc(sp.Text)).Append("</figcaption>");
							body.Append("</figure>");
							break;
						}
						case MdBlockKind.Details: {
							body.Append("<details class=\"mddetails\" data-line=\"").Append(b.SourceLine0).Append('"');
							if (b.DetailsOpen) body.Append(" open");
							body.Append('>');
							body.Append("<summary>");
							if (b.Spans != null && b.Spans.Count > 0)
								appendspans(body, b.Spans, b.Summary, baseFilePath, mapRoot);
							else
								body.Append(enc(string.IsNullOrEmpty(b.Summary) ? "Details" : b.Summary));
							body.Append("</summary><div class=\"mddetails-body\">");
							appendblocks(b.Children);
							body.Append("</div></details>");
							break;
						}
						case MdBlockKind.Html: {
							body.Append("<pre class=\"htmlblock\" data-line=\"").Append(b.SourceLine0).Append("\">")
								.Append(enc(b.Text ?? "")).Append("</pre>");
							break;
						}
					}
				}
			}

			appendblocks(doc.Blocks);
		}

		var scale = fontScale > 0.2 ? fontScale : 1.0;
		var fs = (14 * scale).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
		var sb = new StringBuilder(body.Length + 1800);
		sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/>");
		sb.Append("<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"/>");
		sb.Append("<style>");
		sb.Append("html,body{margin:0;padding:0;background:#fff;color:#111827;}");
		sb.Append("body{font-family:'Segoe UI','Segoe UI Emoji','Microsoft YaHei UI','微软雅黑',sans-serif;");
		sb.Append("font-size:").Append(fs).Append("px;line-height:1.55;padding:20px 28px 40px;");
		sb.Append("word-wrap:break-word;overflow-wrap:break-word;}");
		sb.Append("h1,h2,h3,h4,h5,h6{font-weight:600;line-height:1.3;margin:1.1em 0 .5em;scroll-margin-top:20px;}");
		sb.Append("h1{font-size:2em;border-bottom:1px solid #d1d5db;padding-bottom:.35em;}");
		sb.Append("h2{font-size:1.55em;border-bottom:1px solid #e5e7eb;padding-bottom:.3em;}");
		sb.Append("h3{font-size:1.25em;}h4{font-size:1.1em;}h5,h6{font-size:1em;color:#374151;}");
		sb.Append(".hnum{color:#6b7280;font-weight:600;margin-right:.15em;}");
		sb.Append("p{margin:0 0 .75em;}");
		sb.Append("a{color:#2563eb;text-decoration:none;}a:hover{text-decoration:underline;}");
		sb.Append("code{font-family:Consolas,'Cascadia Mono',monospace;font-size:.92em;");
		sb.Append("background:#f3f4f6;padding:.1em .35em;border-radius:3px;}");
		sb.Append("pre{margin:0;overflow:auto;}pre code{display:block;padding:10px 12px;background:transparent;line-height:1.45;}");
		sb.Append(".codewrap{background:#f3f4f6;border:1px solid #d1d5db;border-radius:4px;margin:.5em 0 .9em;}");
		sb.Append(".codehead{display:flex;justify-content:space-between;align-items:center;");
		sb.Append("padding:6px 10px 2px;font-size:11px;color:#4b5563;font-style:italic;}");
		// 超长代码块：默认约 15 行，按钮展开/收起
		sb.Append(".codefold{appearance:none;-webkit-appearance:none;border:1px solid #d1d5db;background:#fff;");
		sb.Append("color:#2563eb;font-size:11px;line-height:1.3;padding:2px 8px;border-radius:3px;");
		sb.Append("cursor:pointer;font-style:normal;font-family:inherit;}");
		sb.Append(".codefold:hover{background:#eff6ff;border-color:#93c5fd;}");
		sb.Append(".codewrap.collapsible:not(.expanded) pre.codebody{");
		sb.Append("max-height:calc(1.45em * ").Append(CodeFoldLines).Append(" + 20px);");
		sb.Append("overflow:hidden;position:relative;}");
		sb.Append(".codewrap.collapsible:not(.expanded) pre.codebody::after{content:'';");
		sb.Append("position:absolute;left:0;right:0;bottom:0;height:2.4em;");
		sb.Append("background:linear-gradient(transparent,#f3f4f6);pointer-events:none;}");
		sb.Append(".codewrap.collapsible.expanded pre.codebody{max-height:none;overflow:auto;}");
		sb.Append("blockquote{margin:.75em 0 1em;padding:.55em 1em .55em 1em;border-left:4px solid #9ca3af;");
		sb.Append("background:#f3f4f6;color:#4b5563;font-style:italic;}");
		sb.Append("hr{border:0;border-top:2px solid #d1d5db;margin:1.25em 0;width:100%;}");
		sb.Append("ul,ol{margin:.2em 0 .8em;padding-left:").Append(tabSize)
			.Append("em;}li{margin:.15em 0;}");
		// 列表项：margin-left 按 indent_cols×0.6em（见 Build）；mdind 作像素兜底
		sb.Append(".mdli{margin-top:.12em;margin-bottom:.12em;line-height:1.45;}");
		sb.Append(".mdmark{display:inline-block;min-width:1.25em;margin-right:.4em;");
		sb.Append("color:#374151;}");
		sb.Append(".mdind{display:inline-block;height:1em;vertical-align:top;}");
		// GFM 任务列表：纯 CSS 方框（预览只读）
		sb.Append(".mdcb{display:inline-block;width:1em;height:1em;margin:0 .45em 0 0;");
		sb.Append("border:1.5px solid #6b7280;border-radius:3px;vertical-align:-.12em;");
		sb.Append("box-sizing:border-box;position:relative;background:#fff;}");
		sb.Append(".mdcb.on{background:#2563eb;border-color:#2563eb;}");
		sb.Append(".mdcb.on::after{content:'';position:absolute;left:.28em;top:.02em;");
		sb.Append("width:.28em;height:.55em;border:solid #fff;border-width:0 2px 2px 0;");
		sb.Append("transform:rotate(45deg);}");
		// 列宽由 colgroup 指定；短表 width 用 px（不撑满），长表 width:100%
		sb.Append("table{border-collapse:collapse;max-width:100%;table-layout:fixed;margin:.5em 0 .9em;");
		sb.Append("box-sizing:border-box;}");
		sb.Append("th,td{border:1px solid #d1d5db;padding:6px 8px;text-align:left;vertical-align:top;");
		sb.Append("box-sizing:border-box;");
		// 勿 break-word/anywhere：E2026108 等无空格串会被从中间拆行
		sb.Append("overflow-wrap:normal;word-break:normal;}");
		sb.Append("th.nowrap,td.nowrap{white-space:nowrap;}");
		sb.Append("thead th{background:#f9fafb;font-weight:600;}");
		sb.Append(".tablewrap{max-width:100%;overflow-x:auto;margin:.5em 0 .9em;");
		sb.Append("box-sizing:border-box;}");
		sb.Append(".tablewrap>table{margin:0;max-width:100%;}");
		sb.Append("img{max-width:100%;height:auto;display:block;margin:.4em auto;}");
		sb.Append("figure.htmlimg{margin:.5em 0 .9em;}figure.htmlimg img{margin:.2em auto;}");
		sb.Append("details.mddetails{margin:.5em 0 .9em;border:1px solid #e5e7eb;border-radius:6px;");
		sb.Append("padding:.35em .85em .55em;background:#fafafa;}");
		sb.Append("details.mddetails>summary{cursor:pointer;font-weight:600;user-select:none;");
		sb.Append("list-style:none;outline:none;}");
		sb.Append("details.mddetails>summary::-webkit-details-marker{display:none;}");
		sb.Append("details.mddetails>summary::before{content:'▸';display:inline-block;");
		sb.Append("width:1.1em;color:#6b7280;}");
		sb.Append("details.mddetails[open]>summary::before{content:'▼';}");
		sb.Append("details.mddetails[open]>summary{margin-bottom:.45em;}");
		sb.Append(".mddetails-body{padding:.1em 0 .2em;}");
		sb.Append("figure{margin:.6em 0 .9em;}figcaption{text-align:center;font-size:12px;color:#4b5563;margin-top:4px;}");
		sb.Append("mark,.findhit{background:#fef08a;padding:0 .1em;}");
		sb.Append(".htmlblock{font-size:12px;color:#4b5563;background:#f9fafb;padding:8px;border-radius:4px;}");
		sb.Append(".empty{color:#6b7280;}");
		sb.Append("del,s{text-decoration:line-through;}");
		sb.Append(".mermaidwrap{margin:.7em 0 1.1em;overflow-x:auto;text-align:center;");
		sb.Append("background:#fafafa;border:1px solid #e5e7eb;border-radius:6px;padding:12px 8px;}");
		sb.Append(".mermaidwrap .mermaid{margin:0;background:transparent;}");
		sb.Append(".mermaidwrap svg{max-width:100%;height:auto;}");
		// highlight.js：覆盖默认 pre 底，避免与 .codewrap 叠色
		sb.Append(".codewrap pre code.hljs{background:transparent;padding:10px 12px;}");
		sb.Append("</style>");
		if (hasCodeHl) {
			sb.Append("<link rel=\"stylesheet\" href=\"https://").Append(StaticHost)
				.Append("/highlight-github.min.css\" ");
			sb.Append("onerror=\"this.onerror=null;this.href='").Append(HighlightCssCdn).Append("';\">");
		}
		sb.Append("</head><body>");
		sb.Append(body);
		if (hasMermaid) {
			sb.Append("<script src=\"https://").Append(StaticHost).Append("/mermaid.min.js\" ");
			sb.Append("onerror=\"this.onerror=null;this.src='").Append(MermaidCdn).Append("';\"></script>");
			sb.Append("<script>(function(){");
			sb.Append("function boot(){try{");
			sb.Append("if(!window.mermaid)return;");
			sb.Append("mermaid.initialize({startOnLoad:false,securityLevel:'loose',theme:'neutral'});");
			sb.Append("mermaid.run({querySelector:'.mermaid'});");
			sb.Append("}catch(e){console&&console.warn&&console.warn('mermaid',e);}}");
			sb.Append("if(window.mermaid)boot();");
			sb.Append("else{var n=0,t=setInterval(function(){");
			sb.Append("if(window.mermaid||++n>80){clearInterval(t);boot();}},50);}");
			sb.Append("})();</script>");
		}
		if (hasCodeHl) {
			sb.Append("<script src=\"https://").Append(StaticHost).Append("/highlight.min.js\" ");
			sb.Append("onerror=\"this.onerror=null;this.src='").Append(HighlightCdn).Append("';\"></script>");
			sb.Append("<script>(function(){");
			sb.Append("function boot(){try{");
			sb.Append("if(!window.hljs||!hljs.highlightAll)return;");
			sb.Append("hljs.highlightAll();");
			sb.Append("}catch(e){console&&console.warn&&console.warn('hljs',e);}}");
			sb.Append("if(window.hljs)boot();");
			sb.Append("else{var n=0,t=setInterval(function(){");
			sb.Append("if(window.hljs||++n>80){clearInterval(t);boot();}},50);}");
			sb.Append("})();</script>");
		}
		sb.Append("<script>");
		sb.Append("(function(){");
		sb.Append("function post(o){try{if(window.chrome&&chrome.webview)chrome.webview.postMessage(JSON.stringify(o));}catch(e){}}");
		sb.Append("document.addEventListener('click',function(e){");
		// 代码块展开/收起
		sb.Append("var fold=e.target&&e.target.closest?e.target.closest('button.codefold'):null;");
		sb.Append("if(fold){e.preventDefault();e.stopPropagation();");
		sb.Append("var wrap=fold.closest?fold.closest('.codewrap'):null;if(!wrap)return;");
		sb.Append("var on=wrap.classList.toggle('expanded');");
		sb.Append("fold.textContent=on?(fold.getAttribute('data-collapse')||'收起'):(fold.getAttribute('data-expand')||'显示全部');");
		sb.Append("return;}");
		sb.Append("var a=e.target&&e.target.closest?e.target.closest('a'):null;");
		sb.Append("if(!a)return;e.preventDefault();e.stopPropagation();");
		sb.Append("var h=a.getAttribute('data-href')||a.getAttribute('href')||'';");
		sb.Append("post({t:'nav',href:h});},true);");
		// 双击图片 → 宿主全窗预览
		sb.Append("document.addEventListener('dblclick',function(e){");
		sb.Append("var img=e.target&&e.target.closest?e.target.closest('img'):null;");
		sb.Append("if(!img)return;e.preventDefault();e.stopPropagation();");
		sb.Append("post({t:'img',path:img.getAttribute('data-path')||'',src:img.getAttribute('src')||'',alt:img.getAttribute('alt')||''});");
		sb.Append("},true);");
		// 右键图片 → 宿主 WPF 菜单（复制图片/复制为文件/保存）；阻止 WebView 默认菜单冲剪贴板
		sb.Append("document.addEventListener('contextmenu',function(e){");
		sb.Append("var img=e.target&&e.target.closest?e.target.closest('img'):null;");
		sb.Append("if(!img)return;e.preventDefault();e.stopPropagation();");
		sb.Append("post({t:'imgctx',path:img.getAttribute('data-path')||'',");
		sb.Append("src:img.getAttribute('src')||img.src||'',alt:img.getAttribute('alt')||''});");
		sb.Append("},true);");
		sb.Append("var st=null;");
		sb.Append("function outlineLine(){");
		sb.Append("var els=document.querySelectorAll('h1[data-line],h2[data-line],h3[data-line],h4[data-line],h5[data-line],h6[data-line]');");
		sb.Append("var best=-1,margin=28;");
		sb.Append("for(var i=0;i<els.length;i++){var r=els[i].getBoundingClientRect();");
		sb.Append("if(r.top<=margin)best=els[i].getAttribute('data-line')|0;}");
		sb.Append("return best;}");
		// 视口顶部附近最后一个带 data-line 的块（预览↔编辑对齐用）
		sb.Append("function topLine(){");
		sb.Append("var els=document.querySelectorAll('[data-line]');");
		sb.Append("var best=-1,margin=40,fallback=-1,fb=1e9;");
		sb.Append("for(var i=0;i<els.length;i++){var r=els[i].getBoundingClientRect();");
		sb.Append("var ln=els[i].getAttribute('data-line')|0;");
		sb.Append("if(r.top<=margin)best=ln;");
		sb.Append("var d=Math.abs(r.top);if(d<fb){fb=d;fallback=ln;}}");
		sb.Append("return best>=0?best:fallback;}");
		sb.Append("window.addEventListener('scroll',function(){");
		sb.Append("if(st)return;st=setTimeout(function(){st=null;");
		sb.Append("var y=window.scrollY||document.documentElement.scrollTop||0;");
		sb.Append("var max=Math.max(0,(document.documentElement.scrollHeight||0)-(window.innerHeight||0));");
		sb.Append("post({t:'scroll',y:y,max:max,outline:outlineLine(),top:topLine()});},80);},{passive:true});");
		sb.Append("window.mdGetOutlineLine=function(){return outlineLine();};");
		sb.Append("window.mdGetTopLine=function(){return topLine();};");
		sb.Append("window.mdScrollToLine=function(line){");
		sb.Append("line=line|0;var el=document.querySelector('[data-line=\"'+line+'\"]');");
		sb.Append("if(!el){var all=document.querySelectorAll('[data-line]');var best=null,bd=1e9;");
		sb.Append("for(var i=0;i<all.length;i++){var d=Math.abs((all[i].getAttribute('data-line')|0)-line);");
		sb.Append("if(d<bd){bd=d;best=all[i];}}el=best;}");
		sb.Append("if(!el)return false;");
		sb.Append("var pad=20;var y=el.getBoundingClientRect().top+(window.scrollY||document.documentElement.scrollTop||0)-pad;");
		sb.Append("window.scrollTo(0,Math.max(0,y));return true;};");
		sb.Append("window.mdScrollRatio=function(r){");
		sb.Append("r=Math.max(0,Math.min(1,+r||0));");
		sb.Append("var max=Math.max(0,(document.documentElement.scrollHeight||0)-(window.innerHeight||0));");
		sb.Append("window.scrollTo(0,max*r);};");
		sb.Append("window.mdGetScroll=function(){");
		sb.Append("var y=window.scrollY||document.documentElement.scrollTop||0;");
		sb.Append("var max=Math.max(0,(document.documentElement.scrollHeight||0)-(window.innerHeight||0));");
		sb.Append("return JSON.stringify({y:y,max:max});};");
		sb.Append("window.mdClearFind=function(){");
		sb.Append("var marks=document.querySelectorAll('mark.findhit');");
		sb.Append("for(var i=0;i<marks.length;i++){var m=marks[i];var p=m.parentNode;");
		sb.Append("while(m.firstChild)p.insertBefore(m.firstChild,m);p.removeChild(m);p.normalize();}};");
		sb.Append("window.mdHighlightFind=function(q,hitIndex){");
		sb.Append("mdClearFind();if(!q)return false;hitIndex=hitIndex|0;");
		sb.Append("var walker=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT,{");
		sb.Append("acceptNode:function(n){var p=n.parentElement;if(!p)return NodeFilter.FILTER_REJECT;");
		sb.Append("var t=p.tagName;if(t==='SCRIPT'||t==='STYLE')return NodeFilter.FILTER_REJECT;");
		sb.Append("return NodeFilter.FILTER_ACCEPT;}});");
		sb.Append("var nodes=[],n;while(n=walker.nextNode())nodes.push(n);");
		sb.Append("var ql=q.toLowerCase(),seen=0;");
		sb.Append("for(var i=0;i<nodes.length;i++){var text=nodes[i].nodeValue||'';");
		sb.Append("var low=text.toLowerCase(),from=0;");
		sb.Append("while(true){var j=low.indexOf(ql,from);if(j<0)break;");
		sb.Append("if(seen===hitIndex){var range=document.createRange();");
		sb.Append("range.setStart(nodes[i],j);range.setEnd(nodes[i],j+q.length);");
		sb.Append("var mark=document.createElement('mark');mark.className='findhit';");
		sb.Append("try{range.surroundContents(mark);}catch(e){return false;}");
		sb.Append("mark.scrollIntoView({block:'center',behavior:'auto'});return true;}");
		sb.Append("seen++;from=j+Math.max(1,q.length);}}return false;};");
		sb.Append("})();");
		sb.Append("</script></body></html>");
		return sb.ToString();
	}

	/// <summary>
	/// 表格：MdTableLayout.AllocateColumnsDip（短表按内容宽，长表铺满并换行）。
	/// </summary>
	static void appendtable(StringBuilder body, MdBlock b, double pageWidth, string baseFilePath, string assetRoot) {
		body.Append("<div class=\"tablewrap\">");
		if (b.TableRows == null || b.TableRows.Count == 0) {
			body.Append("<table data-line=\"").Append(b.SourceLine0).Append("\"></table></div>");
			return;
		}
		var cols = 0;
		foreach (var r in b.TableRows)
			if (r != null && r.Length > cols) cols = r.Length;
		if (cols <= 0) {
			body.Append("<table data-line=\"").Append(b.SourceLine0).Append("\"></table></div>");
			return;
		}

		const double cellPadH = 16;
		const double pagePadH = 56;
		var avail = Math.Max(cols * 28.0, pageWidth - pagePadH);
		var colDip = MdTableLayout.AllocateColumnsDip(
			b.TableRows, cols, pageWidth,
			unitDip: MdTableLayout.DEFAULT_UNIT_DIP,
			pagePadH: pagePadH,
			cellPadH: cellPadH);
		double sum = 0;
		if (colDip != null)
			foreach (var w in colDip) sum += w;
		var shrink = sum > 0.5 && sum < avail - 0.5;
		var nowrap = MdTableLayout.ShortNoWrapColumns(b.TableRows, cols);

		body.Append("<table data-line=\"").Append(b.SourceLine0).Append('"');
		if (shrink) {
			// 内容总宽不足：表宽=列宽之和（px），不撑满预览区
			body.Append(" style=\"width:")
				.Append(sum.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
				.Append("px\"");
		} else {
			body.Append(" style=\"width:100%\"");
		}
		body.Append('>');

		body.Append("<colgroup>");
		for (var c = 0; c < cols; c++) {
			var w = colDip != null && c < colDip.Length ? colDip[c] : 40;
			if (shrink) {
				body.Append("<col style=\"width:")
					.Append(w.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
					.Append("px\"/>");
			} else {
				double pct = sum > 0.5 ? w / sum * 100.0 : 100.0 / cols;
				if (nowrap != null && c < nowrap.Length && nowrap[c])
					pct = Math.Ceiling(pct * 10) / 10.0;
				body.Append("<col style=\"width:")
					.Append(pct.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
					.Append("%\"/>");
			}
		}
		body.Append("</colgroup>");

		for (var ri = 0; ri < b.TableRows.Count; ri++) {
			var cells = b.TableRows[ri] ?? Array.Empty<string>();
			body.Append(ri == 0 ? "<thead><tr>" : (ri == 1 ? "<tbody><tr>" : "<tr>"));
			var tag = ri == 0 ? "th" : "td";
			for (var ci = 0; ci < cols; ci++) {
				var txt = ci < cells.Length ? cells[ci] : "";
				var align = "left";
				if (b.TableAlign != null && ci < b.TableAlign.Count)
					align = b.TableAlign[ci];
				body.Append('<').Append(tag);
				var cls = nowrap != null && ci < nowrap.Length && nowrap[ci] ? "nowrap" : null;
				if (cls != null || align != "left") {
					body.Append(' ');
					if (cls != null)
						body.Append("class=\"").Append(cls).Append('"');
					if (align != "left") {
						if (cls != null) body.Append(' ');
						body.Append("style=\"text-align:").Append(align).Append('"');
					}
				}
				body.Append('>');
				appendspans(body, MdParser.ParseInlines(txt), txt, baseFilePath, assetRoot);
				body.Append("</").Append(tag).Append('>');
			}
			body.Append("</tr>");
			if (ri == 0) body.Append("</thead>");
		}
		if (b.TableRows.Count > 1) body.Append("</tbody>");
		body.Append("</table></div>");
	}

	static void appendspans(StringBuilder sb, List<MdSpan> spans, string fallback, string baseFilePath, string assetRoot) {
		if (spans == null || spans.Count == 0) {
			if (!string.IsNullOrEmpty(fallback))
				sb.Append(enc(fallback));
			return;
		}
		foreach (var sp in spans) {
			if (sp == null) continue;
			switch (sp.Kind) {
				case "bold":
					sb.Append("<strong>").Append(enc(sp.Text)).Append("</strong>");
					break;
				case "italic":
					sb.Append("<em>").Append(enc(sp.Text)).Append("</em>");
					break;
				case "code":
					sb.Append("<code>").Append(enc(sp.Text)).Append("</code>");
					break;
				case "strike":
					sb.Append("<del>").Append(enc(sp.Text)).Append("</del>");
					break;
				case "mark":
					sb.Append("<mark>").Append(enc(sp.Text)).Append("</mark>");
					break;
				case "softbr":
					sb.Append("<br/>");
					break;
				case "link": {
					var href = sp.Href ?? "";
					sb.Append("<a href=\"#\" data-href=\"").Append(encattr(href)).Append("\">")
						.Append(enc(string.IsNullOrEmpty(sp.Text) ? href : sp.Text))
						.Append("</a>");
					break;
				}
				case "image":
					appendimage(sb, sp, baseFilePath, assetRoot, block: false);
					break;
				default:
					sb.Append(enc(sp.Text));
					break;
			}
		}
	}

	static void appendimage(StringBuilder sb, MdSpan sp, string baseFilePath, string assetRoot, bool block,
		double? widthPx = null, double? heightPx = null) {
		var href = (sp?.Href ?? "").Trim();
		var alt = sp?.Text ?? "";
		var src = resolveimgsrc(href, baseFilePath, assetRoot);
		if (string.IsNullOrEmpty(src)) {
			sb.Append("<a href=\"#\" data-href=\"").Append(encattr(href)).Append("\">")
				.Append("🖼 ").Append(enc(string.IsNullOrEmpty(alt) ? "image" : alt))
				.Append("</a>");
			return;
		}
		sb.Append("<img src=\"").Append(encattr(src)).Append("\" alt=\"").Append(encattr(alt)).Append('"');
		// 本地绝对路径：宿主双击预览时优先用 file 加载
		try {
			var localPath = MdViewer.ResolveHrefPath(baseFilePath, href);
			if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
				sb.Append(" data-path=\"").Append(encattr(localPath)).Append('"');
		} catch { /* ignore */ }
		sb.Append(" title=\"双击预览 · 右键复制/保存\"");
		var style = new StringBuilder();
		if (widthPx != null && widthPx.Value > 0)
			style.Append("width:").Append(widthPx.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append("px;");
		if (heightPx != null && heightPx.Value > 0)
			style.Append("height:").Append(heightPx.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append("px;");
		if (!block)
			style.Append("display:inline-block;max-height:200px;margin:0 4px;vertical-align:middle;cursor:zoom-in;");
		else if (widthPx == null)
			style.Append("max-width:100%;cursor:zoom-in;");
		else
			style.Append("cursor:zoom-in;");
		if (style.Length > 0)
			sb.Append(" style=\"").Append(style).Append('"');
		sb.Append("/>");
	}

	/// <summary>
	/// 计算 WebView 资源根目录：文档目录与本地图的公共父路径（支持 ../images）。
	/// </summary>
	public static string ComputeAssetRoot(string baseFilePath, MdDoc doc) {
		string root = null;
		try {
			if (!string.IsNullOrEmpty(baseFilePath)) {
				var d = Path.GetDirectoryName(baseFilePath);
				if (!string.IsNullOrEmpty(d))
					root = Path.GetFullPath(d);
			}
		} catch { root = null; }
		if (doc?.Blocks == null) return root;
		var paths = new List<string>();
		collectlocalimages(doc.Blocks, baseFilePath, paths);
		foreach (var p in paths) {
			try {
				var dir = Path.GetDirectoryName(p);
				if (string.IsNullOrEmpty(dir)) continue;
				dir = Path.GetFullPath(dir);
				root = string.IsNullOrEmpty(root) ? dir : commonroot(root, dir);
			} catch { /* ignore */ }
		}
		return root;
	}

	static void collectlocalimages(IList<MdBlock> blocks, string baseFilePath, List<string> sink) {
		if (blocks == null || sink == null) return;
		foreach (var b in blocks) {
			if (b == null) continue;
			if (b.Kind == MdBlockKind.HtmlImg && !string.IsNullOrWhiteSpace(b.Text))
				trypushlocal(b.Text, baseFilePath, sink);
			if (b.Spans != null) {
				foreach (var sp in b.Spans) {
					if (sp != null && sp.Kind == "image")
						trypushlocal(sp.Href, baseFilePath, sink);
				}
			}
			if (b.TableRows != null) {
				foreach (var row in b.TableRows) {
					if (row == null) continue;
					foreach (var cell in row) {
						if (string.IsNullOrEmpty(cell)) continue;
						var spans = MdParser.ParseInlines(cell);
						if (spans == null) continue;
						foreach (var sp in spans) {
							if (sp != null && sp.Kind == "image")
								trypushlocal(sp.Href, baseFilePath, sink);
						}
					}
				}
			}
			if (b.Children != null)
				collectlocalimages(b.Children, baseFilePath, sink);
		}
	}

	static void trypushlocal(string href, string baseFilePath, List<string> sink) {
		if (string.IsNullOrWhiteSpace(href)) return;
		href = href.Trim();
		if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			|| href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
			|| href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
			return;
		try {
			var path = MdViewer.ResolveHrefPath(baseFilePath, href);
			if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
			sink.Add(Path.GetFullPath(path));
		} catch { /* ignore */ }
	}

	static string commonroot(string a, string b) {
		if (string.IsNullOrEmpty(a)) return b;
		if (string.IsNullOrEmpty(b)) return a;
		a = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		b = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var pa = a.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.None);
		var pb = b.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.None);
		var n = Math.Min(pa.Length, pb.Length);
		var i = 0;
		while (i < n && string.Equals(pa[i], pb[i], StringComparison.OrdinalIgnoreCase))
			i++;
		if (i == 0) return a;
		return string.Join(Path.DirectorySeparatorChar.ToString(), pa, 0, i);
	}

	/// <summary>http(s) 原样；本地相对/绝对 → https://md.assets/相对路径（相对 assetRoot）。</summary>
	public static string resolveimgsrc(string href, string baseFilePath, string assetRoot = null) {
		if (string.IsNullOrWhiteSpace(href)) return null;
		href = href.Trim();
		if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			|| href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
			|| href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
			return href;
		var path = MdViewer.ResolveHrefPath(baseFilePath, href);
		if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
		var root = assetRoot;
		if (string.IsNullOrEmpty(root)) {
			root = string.IsNullOrEmpty(baseFilePath) ? null : Path.GetDirectoryName(baseFilePath);
		}
		if (string.IsNullOrEmpty(root))
			return null;
		try {
			var full = Path.GetFullPath(path);
			root = Path.GetFullPath(root);
			if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
				root += Path.DirectorySeparatorChar;
			if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
				return null;
			var rel = full.Substring(root.Length).Replace('\\', '/');
			return "https://" + AssetHost + "/" + rel;
		} catch {
			return null;
		}
	}

	static bool ismermaid(string lang) =>
		!string.IsNullOrEmpty(lang)
		&& string.Equals(lang.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase);

	/// <summary>代码正文行数（去掉末尾一个空行，避免 fence 尾换行虚增）。</summary>
	static int countcodelines(string text) {
		if (string.IsNullOrEmpty(text)) return 0;
		var t = text;
		if (t.EndsWith("\r\n", StringComparison.Ordinal))
			t = t.Substring(0, t.Length - 2);
		else if (t.Length > 0 && (t[t.Length - 1] == '\n' || t[t.Length - 1] == '\r'))
			t = t.Substring(0, t.Length - 1);
		if (t.Length == 0) return 0;
		var n = 1;
		for (var i = 0; i < t.Length; i++)
			if (t[i] == '\n') n++;
		return n;
	}

	static string enc(string s) =>
		string.IsNullOrEmpty(s) ? "" : WebUtility.HtmlEncode(s);

	static string encattr(string s) =>
		enc(s).Replace("\"", "&quot;");
}
