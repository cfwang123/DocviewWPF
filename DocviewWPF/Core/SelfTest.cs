using System;
using System.IO;
using System.Text;

namespace DocviewWPF;

/// <summary>
/// 命令行自检：验证 TXT/MD 解析与编码等真实入口（非假测）。
/// 用法：DocviewWPF.exe --selftest-md
/// 退出码 0=通过，1=失败。
/// </summary>
static class SelfTest {
	public static bool IsSelfTestArg(string[] args) {
		if (args == null) return false;
		foreach (var a in args) {
			if (string.Equals(a, "--selftest-md", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(a, "--selftest-typora-click", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(a, "--selftest-console", StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	public static bool IsTyporaClickArg(string[] args) {
		if (args == null) return false;
		foreach (var a in args)
			if (string.Equals(a, "--selftest-typora-click", StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}

	public static bool IsConsoleArg(string[] args) {
		if (args == null) return false;
		foreach (var a in args)
			if (string.Equals(a, "--selftest-console", StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}

	/// <summary>
	/// Typora 综合自测：点击 ≤300ms、光标行恢复标记、撤销、编辑卡顿 ≤300ms。
	/// 用法：DocviewWPF.exe --selftest-typora-click
	/// </summary>
	public static int RunTyporaClickPerf(TextWriter log) {
		if (log == null) log = Console.Out;
		const int BUDGET_MS = 300;
		const int CLICKS = 80;
		const int EDITS = 40;
		var fail = 0;
		void ok(string name) => log.WriteLine("[PASS] " + name);
		void bad(string name, string detail) {
			fail++;
			log.WriteLine("[FAIL] " + name + " · " + detail);
		}

		// 用固定小文档，断言更稳；同时含标记行
		string mdPath = null;
		var tmpCreated = false;
		try {
			var dir = Path.Combine(Path.GetTempPath(), "DocviewWPF_typora_" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			mdPath = Path.Combine(dir, "typora.md");
			tmpCreated = true;
			var sb = new StringBuilder();
			sb.AppendLine("# Title Line");
			sb.AppendLine();
			sb.AppendLine("plain line without markers");
			sb.AppendLine();
			sb.AppendLine("段落 **粗体** 与 *斜体* 和 `code` 以及 [链接](https://example.com) ==mark== ~~del~~");
			sb.AppendLine();
			for (var i = 0; i < 40; i++) {
				sb.AppendLine($"## 节 {i}");
				sb.AppendLine($"内容 **b{i}** 普通文字");
				sb.AppendLine();
			}
			File.WriteAllText(mdPath, sb.ToString(), new UTF8Encoding(false));
			log.WriteLine("[INFO] md=" + mdPath);

			Exception uiEx = null;
			var maxClick = -1;
			var avgClick = 0.0;
			var maxEdit = -1;
			var avgEdit = 0.0;
			var revealOk = false;
			var undoOk = false;
			var thr = new System.Threading.Thread(() => {
				try {
					var mv = new MdViewer();
					var win = new System.Windows.Window {
						Title = "TyporaSelfTest",
						Width = 960,
						Height = 720,
						Content = mv.View,
						ShowInTaskbar = false,
						WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
					};
					win.Show();
					pumpui(80);
					mv.Load(mdPath);
					pumpui(150);
					mv.EditMode = true;
					mv.EditLayout = MdEditLayout.Typora;
					pumpui(400);

					// 1) 光标移到含 **粗体** 的行 → 标记应可见
					// 行号：0=# Title, 1=空, 2=plain, 3=空, 4=段落 **粗体**...
					mv.MoveCaretToLine(4);
					pumpui(80);
					revealOk = mv.CaretLineShowsMarkers();
					log.WriteLine("[INFO] reveal markers on line4=" + revealOk);

					// 2) 撤销：改文本 → Undo → 还原
					var before = mv.GetRawText();
					mv.MoveCaretToLine(2); // plain line
					pumpui(40);
					mv.InsertTextAtCaret("UNDO_MARKER_XYZ");
					pumpui(120);
					var mid = mv.GetRawText();
					if (mid.IndexOf("UNDO_MARKER_XYZ", StringComparison.Ordinal) < 0)
						throw new Exception("insert failed, text=" + shortz(mid));
					if (!mv.UndoEdit())
						throw new Exception("UndoEdit returned false");
					pumpui(120);
					var after = mv.GetRawText();
					undoOk = string.Equals(before, after, StringComparison.Ordinal);
					log.WriteLine("[INFO] undo restored=" + undoOk
						+ " beforeLen=" + before.Length + " afterLen=" + after.Length);

					// 3) 点击压测（含 conceal toggle）
					log.WriteLine("[INFO] click storm n=" + CLICKS);
					maxClick = mv.PerfTyporaClickStorm(CLICKS, out avgClick);
					log.WriteLine($"[INFO] clickStorm maxMs={maxClick} avgMs={avgClick:F1}");

					// 4) 编辑压测
					log.WriteLine("[INFO] edit storm n=" + EDITS);
					maxEdit = mv.PerfTyporaEditStorm(EDITS, out avgEdit);
					log.WriteLine($"[INFO] editStorm maxMs={maxEdit} avgMs={avgEdit:F1}");

					try { win.Close(); } catch { /* ignore */ }
					try { mv.Dispose(); } catch { /* ignore */ }
				} catch (Exception ex) {
					uiEx = ex;
				}
				try { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch { /* ignore */ }
			});
			thr.SetApartmentState(System.Threading.ApartmentState.STA);
			thr.Start();
			thr.Join(180000);
			if (thr.IsAlive) {
				bad("typora", "timeout");
			} else if (uiEx != null) {
				bad("typora", uiEx.ToString());
			} else {
				if (!revealOk) bad("typora.reveal", "caret line markers not visible");
				else ok("typora.reveal");
				if (!undoOk) bad("typora.undo", "text not restored");
				else ok("typora.undo");
				if (maxClick < 0) bad("typora.click", "no result");
				else if (maxClick > BUDGET_MS)
					bad("typora.click.maxMs", $"maxMs={maxClick} > {BUDGET_MS}");
				else ok($"typora.click maxMs={maxClick} avgMs={avgClick:F1}");
				if (maxEdit < 0) bad("typora.edit", "no result");
				else if (maxEdit > BUDGET_MS)
					bad("typora.edit.maxMs", $"maxMs={maxEdit} > {BUDGET_MS}");
				else ok($"typora.edit maxMs={maxEdit} avgMs={avgEdit:F1}");
			}
		} catch (Exception ex) {
			bad("typora", ex.Message);
		} finally {
			if (tmpCreated && mdPath != null) {
				try {
					var d = Path.GetDirectoryName(mdPath);
					if (!string.IsNullOrEmpty(d) && Directory.Exists(d))
						Directory.Delete(d, true);
				} catch { /* ignore */ }
			}
		}

		log.WriteLine(fail == 0
			? "SELFTEST TYPORA-CLICK OK"
			: "SELFTEST TYPORA-CLICK FAILED count=" + fail);
		return fail;
	}

	/// <summary>返回失败项数（0=全部通过）。</summary>
	public static int RunMd(TextWriter log) {
		if (log == null) log = Console.Out;
		var fail = 0;

		void ok(string name) => log.WriteLine("[PASS] " + name);
		void bad(string name, string detail) {
			fail++;
			log.WriteLine("[FAIL] " + name + " · " + detail);
		}

		// —— DocKind ——
		try {
			if (DocKindUtil.FromPath("a.txt") != DocKind.Txt)
				bad("DocKind.txt", "expected Txt");
			else ok("DocKind.txt");
			if (DocKindUtil.FromPath("b.MD") != DocKind.Md)
				bad("DocKind.md", "expected Md");
			else ok("DocKind.md");
			if (DocKindUtil.FromPath("c.markdown") != DocKind.Md)
				bad("DocKind.markdown", "expected Md");
			else ok("DocKind.markdown");
			if (!DocKindUtil.Filter.Contains("*.md"))
				bad("DocKind.Filter", "missing *.md");
			else ok("DocKind.Filter");
		} catch (Exception ex) {
			bad("DocKind", ex.Message);
		}

		// —— 行内解析（真实 MdParser.ParseInlines）——
		try {
			var spans = MdParser.ParseInlines("hello **bold** and *it* with `code` and [link](https://x.com) ==mark== ~~del~~");
			bool has(string k) {
				foreach (var s in spans)
					if (s.Kind == k) return true;
				return false;
			}
			if (!has("bold")) bad("inline.bold", "no bold span"); else ok("inline.bold");
			if (!has("italic")) bad("inline.italic", "no italic span"); else ok("inline.italic");
			if (!has("code")) bad("inline.code", "no code span"); else ok("inline.code");
			if (!has("link")) bad("inline.link", "no link span"); else ok("inline.link");
			if (!has("mark")) bad("inline.mark", "no mark span"); else ok("inline.mark");
			if (!has("strike")) bad("inline.strike", "no strike span"); else ok("inline.strike");
			string linkHref = null;
			foreach (var s in spans)
				if (s.Kind == "link") { linkHref = s.Href; break; }
			if (linkHref != "https://x.com")
				bad("inline.link.href", "got " + linkHref);
			else ok("inline.link.href");

			// Tab 列宽
			if (MdParser.IndentCols("\t", 3) != 3)
				bad("indent.tab3", MdParser.IndentCols("\t", 3).ToString());
			else ok("indent.tab3");
			if (MdParser.IndentCols("\t\t", 3) != 6)
				bad("indent.tab3x2", MdParser.IndentCols("\t\t", 3).ToString());
			else ok("indent.tab3x2");
			if (MdParser.IndentCols("  ", 3) != 2)
				bad("indent.spaces", MdParser.IndentCols("  ", 3).ToString());
			else ok("indent.spaces");
			var listDoc = MdParser.Parse("- a\n\t- b\n", 3);
			var levels = new System.Collections.Generic.List<int>();
			foreach (var b in listDoc.Blocks)
				if (b.Kind == MdBlockKind.ListItem) levels.Add(b.Level);
			if (levels.Count < 2 || levels[0] != 0 || levels[1] != 3)
				bad("indent.list.level", string.Join(",", levels));
			else ok("indent.list.level");
			var bulletDoc = MdParser.Parse("- a\n\t● b\n\t\t○ c\n", 3);
			var bLevels = new System.Collections.Generic.List<int>();
			foreach (var b in bulletDoc.Blocks)
				if (b.Kind == MdBlockKind.ListItem) bLevels.Add(b.Level);
			if (bLevels.Count < 3 || bLevels[0] != 0 || bLevels[1] != 3 || bLevels[2] != 6)
				bad("indent.list.unicode", string.Join(",", bLevels));
			else ok("indent.list.unicode");
			var expanded = MdParser.ExpandTabs("a\tb", 3);
			if (expanded != "a  b") // col0='a'(1), tab→2 spaces to col3
				bad("expand.tabs", expanded);
			else ok("expand.tabs");
			var fenceKeep = MdParser.ExpandTabs("x\ty\n```\n\tz\n```\n", 3);
			if (fenceKeep.IndexOf('\t') < 0)
				bad("expand.tabs.fence", "fence tab lost");
			else ok("expand.tabs.fence");
			var retarget = MdParser.RetargetLeadingIndent("   a\n      b\n", 3, 6);
			if (retarget != "      a\n            b\n")
				bad("retarget.indent", retarget.Replace("\n", "\\n"));
			else ok("retarget.indent");
			var listHtml = MdHtmlBuilder.Build(listDoc, null, 1.0, 640, 3);
			if (listHtml.IndexOf("padding-left:25px", StringComparison.Ordinal) < 0
				&& listHtml.IndexOf("padding-left:25", StringComparison.Ordinal) < 0)
				bad("html.list.indent", "no padding for nested item");
			else ok("html.list.indent");

			// 源码换行 → softbr（对齐 mdview）
			var brSpans = MdParser.ParseInlines("一行\n二行");
			var hasSoft = false;
			foreach (var s in brSpans)
				if (s != null && s.Kind == "softbr") { hasSoft = true; break; }
			if (!hasSoft) bad("inline.softbr", "no softbr");
			else ok("inline.softbr");

			var pdoc = MdParser.Parse("alpha\nbeta\n\ngamma");
			MdBlock para0 = null;
			foreach (var b in pdoc.Blocks)
				if (b.Kind == MdBlockKind.Paragraph) { para0 = b; break; }
			var paraSoft = false;
			if (para0?.Spans != null)
				foreach (var s in para0.Spans)
					if (s != null && s.Kind == "softbr") { paraSoft = true; break; }
			if (!paraSoft) bad("block.para.softbr", "newline collapsed");
			else ok("block.para.softbr");

			var htmlBr = MdHtmlBuilder.Build(pdoc, null, 1.0, 640);
			if (htmlBr.IndexOf("<br", StringComparison.OrdinalIgnoreCase) < 0)
				bad("html.br", "missing br");
			else ok("html.br");
			var mdoc = MdParser.Parse("```mermaid\nflowchart LR\n  A-->B\n```\n");
			var mhtml = MdHtmlBuilder.Build(mdoc, null, 1.0, 640);
			if (mhtml.IndexOf("class=\"mermaid\"", StringComparison.Ordinal) < 0)
				bad("html.mermaid", "missing mermaid pre");
			else ok("html.mermaid");
			if (mhtml.IndexOf("md.static/mermaid.min.js", StringComparison.Ordinal) < 0
				&& mhtml.IndexOf("mermaid.min.js", StringComparison.Ordinal) < 0)
				bad("html.mermaid.script", "missing script");
			else ok("html.mermaid.script");
			var cdoc = MdParser.Parse("```csharp\nint x = 1;\n```\n");
			var chtml = MdHtmlBuilder.Build(cdoc, null, 1.0, 640);
			if (chtml.IndexOf("language-csharp", StringComparison.Ordinal) < 0)
				bad("html.code.lang", "missing language class");
			else ok("html.code.lang");
			if (chtml.IndexOf("highlight.min.js", StringComparison.Ordinal) < 0)
				bad("html.code.hljs", "missing highlight script");
			else ok("html.code.hljs");
			if (chtml.IndexOf("highlight-github.min.css", StringComparison.Ordinal) < 0
				&& chtml.IndexOf("styles/github.min.css", StringComparison.Ordinal) < 0)
				bad("html.code.hljs.css", "missing highlight css");
			else ok("html.code.hljs.css");
		} catch (Exception ex) {
			bad("inline", ex.Message);
		}

		// —— 块解析 ——
		try {
			var md = "# Title\n\nPara **x**\n\n- item1\n- item2\n\n```cs\nvar a=1;\n```\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n> quote\n\n---\n";
			var doc = MdParser.Parse(md);
			bool hasKind(MdBlockKind k) {
				foreach (var b in doc.Blocks)
					if (b.Kind == k) return true;
				return false;
			}
			if (!hasKind(MdBlockKind.Heading)) bad("block.heading", "missing"); else ok("block.heading");
			if (!hasKind(MdBlockKind.Paragraph)) bad("block.para", "missing"); else ok("block.para");
			if (!hasKind(MdBlockKind.ListItem)) bad("block.list", "missing"); else ok("block.list");
			var taskDoc = MdParser.Parse("- [ ] open\n- [x] done\n- plain\n");
			MdBlock tOpen = null, tDone = null, tPlain = null;
			foreach (var b in taskDoc.Blocks) {
				if (b.Kind != MdBlockKind.ListItem) continue;
				if (tOpen == null) tOpen = b;
				else if (tDone == null) tDone = b;
				else if (tPlain == null) tPlain = b;
			}
			if (tOpen?.TaskChecked != false || (tOpen.Text ?? "") != "open")
				bad("task.open", (tOpen?.TaskChecked?.ToString() ?? "null") + "/" + tOpen?.Text);
			else ok("task.open");
			if (tDone?.TaskChecked != true || (tDone.Text ?? "") != "done")
				bad("task.done", (tDone?.TaskChecked?.ToString() ?? "null") + "/" + tDone?.Text);
			else ok("task.done");
			if (tPlain?.TaskChecked != null)
				bad("task.plain", tPlain.TaskChecked.ToString());
			else ok("task.plain");
			var taskHtml = MdHtmlBuilder.Build(taskDoc, null, 1.0, 640, 3);
			if (taskHtml.IndexOf("mdcb on", StringComparison.Ordinal) < 0
				|| taskHtml.IndexOf("class=\"mdcb\"", StringComparison.Ordinal) < 0)
				bad("html.task.cb", "missing mdcb");
			else ok("html.task.cb");
			if (taskHtml.IndexOf("[x]", StringComparison.Ordinal) >= 0
				|| taskHtml.IndexOf("[ ]", StringComparison.Ordinal) >= 0)
				bad("html.task.strip", "raw brackets remain");
			else ok("html.task.strip");
			if (!hasKind(MdBlockKind.Code)) bad("block.code", "missing"); else ok("block.code");
			if (!hasKind(MdBlockKind.Table)) bad("block.table", "missing"); else ok("block.table");
			if (!hasKind(MdBlockKind.Quote)) bad("block.quote", "missing"); else ok("block.quote");
			if (!hasKind(MdBlockKind.Hr)) bad("block.hr", "missing"); else ok("block.hr");

			// 源行映射
			var bi = MdParser.BlockIndexForLine(doc, 0);
			if (bi < 0 || doc.Blocks[bi].Kind != MdBlockKind.Heading)
				bad("block.lineMap", "line0 not heading");
			else ok("block.lineMap");

			// 代码语言
			string lang = null;
			foreach (var b in doc.Blocks)
				if (b.Kind == MdBlockKind.Code) { lang = b.Lang; break; }
			if (lang != "cs") bad("block.code.lang", "got " + lang); else ok("block.code.lang");
		} catch (Exception ex) {
			bad("block", ex.Message);
		}

		// —— FlowDocument 构建（源码高亮 / 自检仍用）——
		try {
			var doc = MdParser.Parse("# H\n\nHello [a](https://example.com)\n");
			var fd = MdFlowBuilder.Build(doc, 640);
			if (fd == null || fd.Blocks.Count == 0)
				bad("flow.build", "empty document");
			else ok("flow.build");
			var blk = MdFlowBuilder.FindBlockBySourceLine(fd, 0);
			if (blk == null) bad("flow.findBlock", "null"); else ok("flow.findBlock");
		} catch (Exception ex) {
			bad("flow", ex.Message);
		}

		// —— HTML 预览构建（WebView）——
		try {
			var doc = MdParser.Parse("# Hello\n\npara **x**\n");
			var html = MdHtmlBuilder.Build(doc, null, 1.0, 640);
			if (string.IsNullOrEmpty(html) || html.IndexOf("data-line=\"0\"", StringComparison.Ordinal) < 0)
				bad("html.build", "missing data-line");
			else if (html.IndexOf("<h1", StringComparison.OrdinalIgnoreCase) < 0)
				bad("html.build.h1", "no h1");
			else ok("html.build");
			// 表格 colgroup 应用 MdTableLayout
			var tdoc = MdParser.Parse("|名称|路径|\n|---|---|\n|公司|D:\\a\\very\\long\\path\\file.md|\n");
			var thtml = MdHtmlBuilder.Build(tdoc, null, 1.0, 900);
			if (thtml.IndexOf("<colgroup>", StringComparison.OrdinalIgnoreCase) < 0)
				bad("html.table.colgroup", "missing");
			else ok("html.table.colgroup");

			var idoc = MdParser.Parse("<img src=\"a.png\" style=\"width:120px;height:80px;\" />\n");
			if (idoc.Blocks.Count < 1 || idoc.Blocks[0].Kind != MdBlockKind.HtmlImg)
				bad("html.img.kind", "want HtmlImg");
			else if (idoc.Blocks[0].ImgWidthPx != 120 || idoc.Blocks[0].ImgHeightPx != 80)
				bad("html.img.size", $"w={idoc.Blocks[0].ImgWidthPx} h={idoc.Blocks[0].ImgHeightPx}");
			else ok("html.img.size");

			var ddoc = MdParser.Parse("<details>\n<summary>S</summary>\n\nhello **x**\n\n</details>\n");
			MdBlock det = null;
			foreach (var b in ddoc.Blocks)
				if (b.Kind == MdBlockKind.Details) { det = b; break; }
			if (det == null)
				bad("html.details.kind", "missing");
			else if (det.Summary != "S")
				bad("html.details.summary", det.Summary ?? "");
			else if (det.Children == null || det.Children.Count == 0)
				bad("html.details.children", "empty");
			else {
				var dhtml = MdHtmlBuilder.Build(ddoc, null, 1.0, 640);
				if (dhtml.IndexOf("<details", StringComparison.OrdinalIgnoreCase) < 0
					|| dhtml.IndexOf("<summary>", StringComparison.OrdinalIgnoreCase) < 0)
					bad("html.details.render", "no tags");
				else ok("html.details");
			}
		} catch (Exception ex) {
			bad("html", ex.Message);
		}

		// —— 编码探测（真实 TextFileIo.Decode）——
		try {
			var utf8 = Encoding.UTF8.GetBytes("你好 UTF8");
			var r1 = TextFileIo.Decode(utf8);
			if (r1.Text == null || r1.Text.IndexOf("你好", StringComparison.Ordinal) < 0)
				bad("enc.utf8", "text=" + r1.Text);
			else ok("enc.utf8");

			var bom = new byte[] { 0xEF, 0xBB, 0xBF };
			var withBom = new byte[bom.Length + utf8.Length];
			Buffer.BlockCopy(bom, 0, withBom, 0, bom.Length);
			Buffer.BlockCopy(utf8, 0, withBom, bom.Length, utf8.Length);
			var r2 = TextFileIo.Decode(withBom);
			if (!r2.HasBom) bad("enc.bom", "HasBom=false"); else ok("enc.bom");

			// 非法 UTF-8 字节应落到 GB 系
			var badUtf = new byte[] { 0xC0, 0xAF, 0x41 }; // overlong
			var r3 = TextFileIo.Decode(badUtf);
			if (r3 == null || r3.Text == null) bad("enc.fallback", "null"); else ok("enc.fallback");
		} catch (Exception ex) {
			bad("enc", ex.Message);
		}

		// —— 表格列宽（mdview 算法）——
		try {
			if (MdTableLayout.StrDisplayWidth("ab") != 2)
				bad("table.strw.ascii", MdTableLayout.StrDisplayWidth("ab").ToString());
			else ok("table.strw.ascii");
			if (MdTableLayout.StrDisplayWidth("中") != 2)
				bad("table.strw.cjk", MdTableLayout.StrDisplayWidth("中").ToString());
			else ok("table.strw.cjk");
			// 短内容：sum need ≤ avail → 原样返回，不撑满
			var needSmall = new[] { 4, 6, 3 };
			var gotSmall = MdTableLayout.Allocate(needSmall, 100);
			if (gotSmall[0] != 4 || gotSmall[1] != 6 || gotSmall[2] != 3)
				bad("table.alloc.fit", string.Join(",", gotSmall));
			else ok("table.alloc.fit");
			// 超出：短列优先锁定
			var needBig = new[] { 4, 40, 6 };
			var gotBig = MdTableLayout.Allocate(needBig, 30);
			var sumBig = gotBig[0] + gotBig[1] + gotBig[2];
			if (sumBig != 30)
				bad("table.alloc.tight.sum", sumBig.ToString());
			else if (gotBig[0] > 4 + 1) // 短列不应被放大
				bad("table.alloc.tight.short", string.Join(",", gotBig));
			else ok("table.alloc.tight");
			// ContentNeeds 取列 max；中文「中文」显示宽 4
			var rows = new System.Collections.Generic.List<string[]> {
				new[] { "a", "hello", "x" },
				new[] { "bb", "hi", "中文" },
			};
			var needs = MdTableLayout.ContentNeeds(rows, 3, 40);
			if (needs[0] < 2 || needs[2] < 4)
				bad("table.need", string.Join(",", needs));
			else ok("table.contentNeeds");
			// 图片 need = tableW/ncol（mdview 1/列数）
			var imgNeed = MdTableLayout.CellContentNeed("![alt](a.png)", 40, 4);
			if (imgNeed < 10)
				bad("table.need.img", "want>=10 got=" + imgNeed);
			else ok("table.need.img");
			// 显示宽路径：长路径列 need 远大于短列「名称」
			var pathRows = new System.Collections.Generic.List<string[]> {
				new[] { "名称", "文件" },
				new[] { "公司", @"D:\VS_Projects\我的文件\公司账号.md" },
			};
			var pathNeed = MdTableLayout.ContentNeeds(pathRows, 2, 120);
			if (pathNeed[1] <= pathNeed[0] + 10)
				bad("table.need.path", pathNeed[0] + "," + pathNeed[1]);
			else ok("table.need.path");
			// AllocateColumnsDip：短列钉死、长列更宽；总宽约 100% 视窗
			var colDip = MdTableLayout.AllocateColumnsDip(pathRows, 2, 900);
			if (colDip.Length != 2)
				bad("table.allocCols.len", colDip.Length.ToString());
			else if (colDip[1] <= colDip[0])
				bad("table.allocCols.ratio", colDip[0] + "," + colDip[1]);
			else {
				var sumDip = colDip[0] + colDip[1];
				var want = 900 - 56; // pagePadH
				if (sumDip < want - 8 || sumDip > want + 8)
					bad("table.allocCols.fill", $"sum={sumDip:F0} want≈{want}");
				else ok("table.allocCols.dip+fill100");
			}
			// FormattedText 备用路径仍可用
			var needDip = MdTableLayout.ContentNeedsDip(pathRows, 2, 900, 14);
			if (needDip[1] <= needDip[0] + 40)
				bad("table.needDip.path", needDip[0] + "," + needDip[1]);
			else ok("table.needDip.path");
			var allocDip = MdTableLayout.AllocateDip(needDip, 900);
			if (allocDip[1] < needDip[1] - 1)
				bad("table.allocDip.fit", allocDip[1] + "<" + needDip[1]);
			else ok("table.allocDip.fit");
		} catch (Exception ex) {
			bad("table", ex.Message);
		}

		// —— 链接解析 / slug（真实静态入口）——
		try {
			if (!MdFlowBuilder.IsMdHref("a.md")) bad("link.isMd", "a.md");
			else ok("link.isMd.md");
			if (!MdFlowBuilder.IsMdHref("./x/y.markdown#sec")) bad("link.isMd.anchor", "fail");
			else ok("link.isMd.withAnchor");
			if (MdFlowBuilder.IsMdHref("#only")) bad("link.isMd.hashonly", "should false");
			else ok("link.isMd.hashonly");
			if (MdFlowBuilder.IsMdHref("https://a.com/b.md")) bad("link.isMd.http", "should false");
			else ok("link.isMd.http");
			var baseF = Path.Combine(Path.GetTempPath(), "base", "doc.md");
			var rel = MdViewer.ResolveHrefPath(baseF, "./other.md");
			var want = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "base", "other.md"));
			if (!string.Equals(rel, want, StringComparison.OrdinalIgnoreCase))
				bad("link.resolve", "got=" + rel + " want=" + want);
			else ok("link.resolve.relative");
			// URL 编码路径：无实体文件时返回解码后路径（%20 → 空格）
			var encRel = MdViewer.ResolveHrefPath(baseF, "05%20%E8%BF%9C%E7%A8%8B%E6%9B%B4%E6%96%B0.png");
			var encWant = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "base", "05 远程更新.png"));
			if (!string.Equals(encRel, encWant, StringComparison.OrdinalIgnoreCase))
				bad("link.resolve.urlencoded", "got=" + encRel + " want=" + encWant);
			else ok("link.resolve.urlencoded");
			// 未编码路径：原样
			var plainRel = MdViewer.ResolveHrefPath(baseF, "05 远程更新.png");
			if (!string.Equals(plainRel, encWant, StringComparison.OrdinalIgnoreCase))
				bad("link.resolve.plain", "got=" + plainRel + " want=" + encWant);
			else ok("link.resolve.plain");
			// 磁盘探测：编码链接 → 实际未编码文件名
			var hrefDir = Path.Combine(Path.GetTempPath(), "DocviewWPF_href_" + Guid.NewGuid().ToString("N"));
			try {
				Directory.CreateDirectory(hrefDir);
				var realName = "hello world.png";
				var realPath = Path.Combine(hrefDir, realName);
				File.WriteAllBytes(realPath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
				var baseMd = Path.Combine(hrefDir, "doc.md");
				var got = MdViewer.ResolveHrefPath(baseMd, "hello%20world.png");
				if (!string.Equals(got, Path.GetFullPath(realPath), StringComparison.OrdinalIgnoreCase)
					|| !File.Exists(got))
					bad("link.resolve.exists.encoded", "got=" + got);
				else ok("link.resolve.exists.encoded");
				// 字面文件名含 %20 时优先原文
				var litName = "a%20b.txt";
				var litPath = Path.Combine(hrefDir, litName);
				File.WriteAllText(litPath, "x", Encoding.UTF8);
				var gotLit = MdViewer.ResolveHrefPath(baseMd, "a%20b.txt");
				if (!string.Equals(gotLit, Path.GetFullPath(litPath), StringComparison.OrdinalIgnoreCase))
					bad("link.resolve.literal.pct", "got=" + gotLit);
				else ok("link.resolve.literal.pct");
			} finally {
				try { Directory.Delete(hrefDir, true); } catch { /* ignore */ }
			}
			if (MdViewer.slugify("Hello World", spaceToDash: true) != "hello-world")
				bad("link.slug", MdViewer.slugify("Hello World", true));
			else ok("link.slug");
			// 链接侧：空格不转 -
			if (MdViewer.slugify("Hello World", spaceToDash: false) != "helloworld")
				bad("link.slug.nosp", MdViewer.slugify("Hello World", false));
			else ok("link.slug.nosp");
			if (MdViewer.compactanchor("foo bar") != MdViewer.compactanchor("foo-bar"))
				bad("link.compact", "space vs dash");
			else ok("link.compact");
			if (MdViewer.slugify("中文 标题", spaceToDash: true) != "中文-标题")
				bad("link.slug.cjk", MdViewer.slugify("中文 标题", true));
			else ok("link.slug.cjk");
		} catch (Exception ex) {
			bad("link", ex.Message);
		}

		// —— 端到端：写临时 md/txt → Load 真实 Viewer ——
		var tmpDir = Path.Combine(Path.GetTempPath(), "DocviewWPF_selftest_" + Guid.NewGuid().ToString("N"));
		try {
			Directory.CreateDirectory(tmpDir);
			var mdPath = Path.Combine(tmpDir, "demo.md");
			var txtPath = Path.Combine(tmpDir, "demo.txt");
			// 含标题/粗体/链接/列表，用于断言 conceal 后 Save 不丢标记
			var body = "# SelfTest\n\n**bold** and [link](https://example.com)\n\n- item1\n\n```js\nconst x=1;\n```\n";
			File.WriteAllText(mdPath, body, new UTF8Encoding(false));
			File.WriteAllText(txtPath, "line1\nline2 中文\n", new UTF8Encoding(false));

			// 必须在 STA 线程构造 WPF 控件
			Exception uiEx = null;
			string afterSave = null;
			var thr = new System.Threading.Thread(() => {
				try {
					var mv = new MdViewer();
					mv.Load(mdPath);
					if (mv.Kind != DocKind.Md) throw new Exception("Md Kind");
					if (string.IsNullOrEmpty(mv.Title) || !mv.Title.EndsWith("demo.md", StringComparison.OrdinalIgnoreCase))
						throw new Exception("Md Title=" + mv.Title);
					// 默认预览（非编辑）
					if (mv.EditMode) throw new Exception("Md should open in preview");
					// 查找：预览态可命中且不切入编辑
					var fr0 = mv.Find("bold", true, true, restart: true);
					if (!fr0.Found || fr0.Total < 1)
						throw new Exception("preview find miss: " + fr0.Current + "/" + fr0.Total);
					if (mv.EditMode)
						throw new Exception("Find must not enter EditMode");
					// 编辑态查找不改 layout
					mv.EditMode = true;
					mv.EditLayout = MdEditLayout.Typora;
					pumpui(150);
					var fr1 = mv.Find("item1", true, true, restart: true);
					if (!fr1.Found || fr1.Total < 1)
						throw new Exception("edit find miss");
					if (mv.EditLayout != MdEditLayout.Typora)
						throw new Exception("Find must not change layout, got " + mv.EditLayout);
					mv.ClearFind();
					mv.EditMode = false;
					pumpui(80);
					// 进编辑 + 两种模式
					mv.EditMode = true;
					mv.EditLayout = MdEditLayout.Typora;
					if (!mv.EditMode) throw new Exception("EditMode not set");
					if (mv.EditLayout != MdEditLayout.Typora) throw new Exception("layout typora");
					pumpui(300);
					// 粘贴图片：复制到 images/（有文件名保留名）
					var pngSrc = Path.Combine(tmpDir, "shot.png");
					File.WriteAllBytes(pngSrc, TinyPng1x1);
					var relImg = mv.ImportImageFileForTest(pngSrc);
					if (string.IsNullOrEmpty(relImg) || relImg.IndexOf("images/", StringComparison.OrdinalIgnoreCase) < 0)
						throw new Exception("import image rel=" + relImg);
					var imgFull = Path.Combine(tmpDir, relImg.Replace('/', Path.DirectorySeparatorChar));
					if (!File.Exists(imgFull)) throw new Exception("images file missing: " + imgFull);
					mv.InsertTextAtCaret($"![]({relImg})\n");
					pumpui(100);
					if ((mv.GetRawText() ?? "").IndexOf(relImg, StringComparison.OrdinalIgnoreCase) < 0)
						throw new Exception("md image syntax not in raw");
					ok("viewer.e2e.paste-image");
					mv.EditLayout = MdEditLayout.Code;
					if (mv.EditLayout != MdEditLayout.Code) throw new Exception("layout code");
					pumpui(200);
					mv.EditLayout = MdEditLayout.Side;
					if (mv.EditLayout != MdEditLayout.Side) throw new Exception("layout side");
					pumpui(200);
					mv.EditLayout = MdEditLayout.Typora;
					pumpui(300);
					// 退出编辑再进一次
					mv.EditMode = false;
					pumpui(100);
					mv.EditMode = true;
					mv.EditLayout = MdEditLayout.Typora;
					pumpui(300);
					// 代码块行首 Tab：经 highlight 往返后仍保留
					var bodyWithTab = body + "\n```\n\tindented\n```\n";
					File.WriteAllText(mdPath, bodyWithTab, new UTF8Encoding(false));
					mv.Dispose();
					mv = new MdViewer();
					mv.Load(mdPath);
					if (mv.EditMode) throw new Exception("preview after tab load");
					mv.EditMode = true;
					mv.EditLayout = MdEditLayout.Code;
					pumpui(300);
					// 模拟再写一个 Tab 到文件权威源：Save 前经编辑器往返
					mv.Save();
					var tabRound = File.ReadAllText(mdPath, new UTF8Encoding(false));
					if (tabRound.IndexOf("\tindented", StringComparison.Ordinal) < 0)
						throw new Exception("tab in code fence lost after edit/save: " + shortz(tabRound));
					// insertplaintext 路径：用反射插入 \t 再保存
					var ins = typeof(MdViewer).GetMethod("insertplaintext",
						System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
					if (ins == null) throw new Exception("insertplaintext missing");
					ins.Invoke(mv, new object[] { "\t" });
					pumpui(200);
					mv.Save();
					var afterIns = File.ReadAllText(mdPath, new UTF8Encoding(false));
					if (afterIns.IndexOf('\t') < 0)
						throw new Exception("insertplaintext tab not on disk");

					// 保存后磁盘必须仍含原始 MD 标记（conceal 不得 strip 源码）
					// 恢复干净 body 再测标记
					File.WriteAllText(mdPath, body, new UTF8Encoding(false));
					mv.Dispose();
					mv = new MdViewer();
					mv.Load(mdPath);
					mv.EditMode = true;
					mv.EditLayout = MdEditLayout.Typora;
					pumpui(250);
					mv.Save();
					afterSave = File.ReadAllText(mdPath, new UTF8Encoding(false));
					assertmarkers(afterSave, body);
					mv.Dispose();

					// 二次 Load 仍含标记
					var mv2 = new MdViewer();
					mv2.Load(mdPath);
					if (mv2.EditMode) throw new Exception("reload preview");
					mv2.EditMode = true;
					pumpui(250);
					mv2.Save();
					var after2 = File.ReadAllText(mdPath, new UTF8Encoding(false));
					assertmarkers(after2, body);
					mv2.Dispose();

					var tv = new TextViewer();
					tv.Load(txtPath);
					if (tv.Kind != DocKind.Txt) throw new Exception("Txt Kind");
					if (tv.EditMode) throw new Exception("Txt should open in preview");
					tv.EditMode = true;
					if (!tv.EditMode) throw new Exception("Txt EditMode");
					tv.Save();
					tv.Dispose();

					// ViewerFactory
					using (var v1 = ViewerFactory.Create(DocKind.Txt)) {
						v1.Load(txtPath);
						if (v1.Kind != DocKind.Txt) throw new Exception("factory txt");
					}
					using (var v2 = ViewerFactory.Create(DocKind.Md)) {
						v2.Load(mdPath);
						if (v2.Kind != DocKind.Md) throw new Exception("factory md");
					}

					// ShellLink：创建 .lnk 指向 demo.md 再 Resolve
					var lnkPath = Path.Combine(tmpDir, "demo.md.lnk");
					createlnk(lnkPath, mdPath);
					var resolved = ShellLink.Resolve(lnkPath);
					if (!string.Equals(
						Path.GetFullPath(resolved ?? ""),
						Path.GetFullPath(mdPath),
						StringComparison.OrdinalIgnoreCase))
						throw new Exception("ShellLink resolve got=" + resolved + " want=" + mdPath);
					if (DocKindUtil.FromPath(resolved) != DocKind.Md)
						throw new Exception("resolved kind not Md");
				} catch (Exception ex) {
					uiEx = ex;
				}
				try { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch { /* ignore */ }
			});
			thr.SetApartmentState(System.Threading.ApartmentState.STA);
			thr.Start();
			thr.Join(90000);
			if (thr.IsAlive) {
				bad("viewer.e2e", "timeout");
			} else if (uiEx != null) {
				bad("viewer.e2e", uiEx.ToString());
			} else {
				ok("viewer.e2e.MdViewer+TextViewer+Factory");
				ok("viewer.e2e.find-no-mode-switch");
				ok("viewer.e2e.conceal-preserve-markers");
				ok("viewer.e2e.tab-preserve+insertplaintext");
				ok("viewer.e2e.shelllink-resolve");
			}
		} catch (Exception ex) {
			bad("viewer.e2e", ex.Message);
		} finally {
			try {
				if (Directory.Exists(tmpDir))
					Directory.Delete(tmpDir, true);
			} catch { /* ignore */ }
		}

		log.WriteLine(fail == 0
			? "SELFTEST OK"
			: "SELFTEST FAILED count=" + fail);
		return fail;
	}

	/// <summary>泵 WPF 消息，让 highlight debounce / layout 完成。</summary>
	static void pumpui(int ms) {
		var end = Environment.TickCount + Math.Max(50, ms);
		var frame = new System.Windows.Threading.DispatcherFrame();
		System.Windows.Threading.DispatcherTimer t = null;
		t = new System.Windows.Threading.DispatcherTimer(
			System.Windows.Threading.DispatcherPriority.Background) {
			Interval = TimeSpan.FromMilliseconds(20),
		};
		t.Tick += (_, _) => {
			if (Environment.TickCount - end >= 0) {
				t.Stop();
				frame.Continue = false;
			}
		};
		t.Start();
		try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
		catch { System.Threading.Thread.Sleep(ms); }
	}

	/// <summary>磁盘/字符串必须仍含关键 MD 标记（相对原始 body）。</summary>
	static void assertmarkers(string got, string original) {
		if (got == null) throw new Exception("after save null");
		// 规范化换行再比关键子串
		got = got.Replace("\r\n", "\n").Replace('\r', '\n');
		original = (original ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
		void need(string frag) {
			if (got.IndexOf(frag, StringComparison.Ordinal) < 0)
				throw new Exception("missing marker fragment after save: " + frag + " | got=" + shortz(got));
		}
		need("# SelfTest");
		need("**bold**");
		need("[link](https://example.com)");
		need("- item1");
		need("```js");
		// 不得变成 strip 后的伪源码
		if (got.IndexOf("**bold**", StringComparison.Ordinal) < 0 && got.IndexOf("bold", StringComparison.Ordinal) >= 0)
			throw new Exception("bold markers stripped");
		// 与原文在去掉末尾多余空行后应实质一致
		var a = original.TrimEnd('\n');
		var b = got.TrimEnd('\n');
		if (!string.Equals(a, b, StringComparison.Ordinal)) {
			// 允许仅末尾换行差异；其它必须一致
			if (!string.Equals(a + "\n", got, StringComparison.Ordinal)
				&& !string.Equals(original, got, StringComparison.Ordinal))
				throw new Exception("source diverged after conceal/save.\n--- original ---\n" + shortz(a)
					+ "\n--- got ---\n" + shortz(b));
		}
	}

	static string shortz(string s) {
		if (s == null) return "";
		s = s.Replace("\n", "\\n");
		return s.Length <= 200 ? s : s.Substring(0, 200) + "…";
	}

	/// <summary>1×1 透明 PNG（自检粘贴图片用）。</summary>
	static readonly byte[] TinyPng1x1 = {
		0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
		0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
		0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00,
		0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
		0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49,
		0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
	};

	/// <summary>用 WScript.Shell 创建 .lnk（与 ShellLink.Resolve 同源 COM）。</summary>
	static void createlnk(string lnkPath, string targetPath) {
		var t = Type.GetTypeFromProgID("WScript.Shell");
		if (t == null) throw new Exception("WScript.Shell unavailable");
		object shell = null;
		object sc = null;
		try {
			shell = Activator.CreateInstance(t);
			sc = t.InvokeMember("CreateShortcut",
				System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
			sc.GetType().InvokeMember("TargetPath",
				System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { targetPath });
			sc.GetType().InvokeMember("Save",
				System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
		} finally {
			try {
				if (sc != null && System.Runtime.InteropServices.Marshal.IsComObject(sc))
					System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
			} catch { /* ignore */ }
			try {
				if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
					System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
			} catch { /* ignore */ }
		}
	}

	// ――― 命令行 / ConPTY / VT 自检 ―――

	/// <summary>
	/// ConPTY 读写 + VT 缓冲 + WPF ConsoleViewer 端到端。
	/// 用法：DocviewWPF.exe --selftest-console
	/// </summary>
	public static int RunConsole(TextWriter log) {
		if (log == null) log = Console.Out;
		// 自检结果另写文件，避免依赖 stdout 重定向（重定向会破坏 ConPTY 子进程）
		TextWriter fileLog = null;
		try {
			var dir = Path.Combine(Path.GetTempPath(), "DocviewWPF_selftest");
			Directory.CreateDirectory(dir);
			var path = Path.Combine(dir, "console_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
			fileLog = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
			log.WriteLine("[INFO] also log to " + path);
		} catch { /* ignore */ }

		var fail = 0;
		void line(string s) {
			try { log.WriteLine(s); } catch { /* ignore */ }
			try { fileLog?.WriteLine(s); } catch { /* ignore */ }
		}
		void ok(string name) => line("[PASS] " + name);
		void bad(string name, string detail) {
			fail++;
			line("[FAIL] " + name + " · " + detail);
			try { DocLog.Warn("selftest-console FAIL " + name + " " + detail); } catch { /* ignore */ }
		}
		void info(string s) {
			line("[INFO] " + s);
			try { DocLog.Info("selftest-console " + s); } catch { /* ignore */ }
		}

		// 脱离父控制台，避免 cmd 抢写父 stdout（重定向/调试器场景）
		try { FreeConsole(); } catch { /* ignore */ }

		info("IsSupported=" + ConPtySession.IsSupported);
		if (!ConPtySession.IsSupported) {
			bad("conpty.supported", "CreatePseudoConsole missing (need Win10 1809+)");
			log.WriteLine("SELFTEST FAILED count=" + fail);
			return fail;
		}
		ok("conpty.supported");

		// 1) 纯 ConPTY：启动 cmd，等欢迎输出，echo 标记
		try {
			var raw = new StringBuilder();
			var rawLock = new object();
			using (var s = new ConPtySession()) {
				s.DataReceived += data => {
					if (data == null) return;
					lock (rawLock) raw.Append(Encoding.UTF8.GetString(data));
				};
				var cwd = Path.GetTempPath();
				// 纯 cmd，无 /K chcp（避免挂住）
				s.Start("cmd.exe", null, cwd, 80, 24);
				info("pid=" + s.ProcessId + " start rawLen wait…");

				// 等提示符：至少有可打印字符
				var t0 = Environment.TickCount;
				while (Environment.TickCount - t0 < 5000) {
					string snap;
					lock (rawLock) snap = raw.ToString();
					if (snap.IndexOf('>') >= 0 || snap.Length > 80) break;
					System.Threading.Thread.Sleep(40);
				}
				// 再稳一会儿
				System.Threading.Thread.Sleep(200);
				string snap1;
				lock (rawLock) snap1 = raw.ToString();
				info($"after-start bytesRead={s.BytesRead} chunks={s.ReadChunks} rawLen={snap1.Length} exited={s.HasExited} errR={s.LastReadError} errW={s.LastWriteError}");
				info("snap1=" + trunc(visible(snap1), 220));
				if (s.BytesRead <= 0 && snap1.Length == 0)
					bad("conpty.output", "no data from cmd within 5s");
				else
					ok("conpty.output");

				var marker = "DVWPF_CONSOLE_" + Guid.NewGuid().ToString("N").Substring(0, 8);
				// ConPTY 下用 \r\n 更稳
				var cmd = "echo " + marker + "\r\n";
				var wOk = s.WriteSync(Encoding.UTF8.GetBytes(cmd));
				info($"WriteSync ok={wOk} written={s.BytesWritten} errW={s.LastWriteError}");
				if (!wOk) bad("conpty.write", s.LastWriteError ?? "WriteSync false");
				else ok("conpty.write");

				var t1 = Environment.TickCount;
				var found = false;
				while (Environment.TickCount - t1 < 6000) {
					string snap;
					lock (rawLock) snap = raw.ToString();
					if (snap.IndexOf(marker, StringComparison.Ordinal) >= 0) {
						found = true;
						break;
					}
					System.Threading.Thread.Sleep(40);
				}
				string snap2;
				lock (rawLock) snap2 = raw.ToString();
				info($"after-echo found={found} rawLen={snap2.Length} bytesRead={s.BytesRead} written={s.BytesWritten}");
				info("snap2=" + trunc(visible(snap2), 280));
				if (!found) bad("conpty.echo", "marker not in output: " + marker);
				else ok("conpty.echo");
			}
		} catch (Exception ex) {
			bad("conpty.session", ex.ToString());
		}

		// 2) VT 解析
		try {
			var term = new TerminalControl();
			term.Measure(new System.Windows.Size(640, 400));
			term.Arrange(new System.Windows.Rect(0, 0, 640, 400));
			term.Reset();
			term.FeedSync(Encoding.UTF8.GetBytes("\x1b[2J\x1b[HHello_VT_123\r\nLine2\r\n"));
			var dump = term.DumpScreenText();
			info("vt.dump=" + trunc(visible(dump), 120));
			if (dump.IndexOf("Hello_VT_123", StringComparison.Ordinal) < 0)
				bad("vt.parse", "Hello_VT_123 missing");
			else
				ok("vt.parse");
			if (dump.IndexOf("Line2", StringComparison.Ordinal) < 0)
				bad("vt.newline", "Line2 missing");
			else
				ok("vt.newline");

			var got = new StringBuilder();
			term.Output += b => {
				if (b != null) got.Append(Encoding.UTF8.GetString(b));
			};
			term.HandleKeyDown(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.None);
			term.HandleKeyDown(System.Windows.Input.Key.Return, System.Windows.Input.ModifierKeys.None);
			var outStr = got.ToString();
			info("key.out=" + visible(outStr));
			if (outStr.IndexOf('a') < 0 && outStr.IndexOf('A') < 0)
				bad("vt.key.a", "no a/A emitted, got=" + visible(outStr));
			else
				ok("vt.key.a");
			if (outStr.IndexOf('\r') < 0)
				bad("vt.key.enter", "no CR");
			else
				ok("vt.key.enter");
			term.DisposeResources();
		} catch (Exception ex) {
			bad("vt.unit", ex.ToString());
		}

		// 3) 固定 VT 串：模拟 cmd 横幅（含中文宽字符）+ 截图
		try {
			Exception shotEx = null;
			string shotPath = null;
			string cellsDbg = null;
			string screenTxt = null;
			var thrShot = new System.Threading.Thread(() => {
				try {
					var win = new System.Windows.Window {
						Title = "selftest-console-shot",
						Width = 1280,
						Height = 720,
						WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
						Left = 40,
						Top = 40,
						ShowInTaskbar = false,
					};
					var term = new TerminalControl();
					win.Content = term;
					win.Show();
					pumpui(150);
					// 强制大尺寸（接近用户 162 列）
					term.Measure(new System.Windows.Size(1200, 600));
					term.Arrange(new System.Windows.Rect(0, 0, 1200, 600));
					term.Reset();
					// 模拟 ConPTY 输出的 cmd 横幅（含中文 + 路径）
					var banner =
						"\x1b[2J\x1b[m\x1b[H" +
						"Microsoft Windows [版本 10.0.19045.6466]\r\n" +
						"(c) Microsoft Corporation。保留所有权利。\r\n" +
						"\r\n" +
						@"D:\VS_Projects\学习\leetcode>" +
						"\r\n";
					term.FeedSync(Encoding.UTF8.GetBytes(banner));
					pumpui(100);
					// 强制重绘
					term.InvalidateVisual();
					pumpui(100);
					screenTxt = term.DumpScreenText();
					cellsDbg = term.DumpCellsDebug(4);
					var png = term.CapturePng();
					var dir = Path.Combine(Path.GetTempPath(), "DocviewWPF_selftest");
					Directory.CreateDirectory(dir);
					shotPath = Path.Combine(dir, "console_banner_" + DateTime.Now.ToString("HHmmss") + ".png");
					if (png != null && png.Length > 0)
						File.WriteAllBytes(shotPath, png);
					// 也拷到工作区 tmp
					try {
						var local = Path.Combine(
							Path.GetDirectoryName(typeof(SelfTest).Assembly.Location) ?? ".",
							"..", "..", "..", "..", "tmp");
						local = Path.GetFullPath(local);
						Directory.CreateDirectory(local);
						File.WriteAllBytes(Path.Combine(local, "console_banner.png"), png ?? Array.Empty<byte>());
						File.WriteAllText(Path.Combine(local, "console_banner.txt"),
							(screenTxt ?? "") + "\n---cells---\n" + (cellsDbg ?? ""),
							new UTF8Encoding(false));
						info("shot.local=" + Path.Combine(local, "console_banner.png"));
					} catch (Exception ex2) {
						info("shot.local fail " + ex2.Message);
					}
					try { term.DisposeResources(); } catch { /* ignore */ }
					try { win.Close(); } catch { /* ignore */ }

					// ConsoleViewer 整页截图：IME 聚焦 + 提示符后应无闪烁光标
					var win2 = new System.Windows.Window {
						Title = "selftest-console-full",
						Width = 1100,
						Height = 640,
						WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
						Left = 60,
						Top = 60,
						ShowInTaskbar = false,
						Background = System.Windows.Media.Brushes.Black,
					};
					var cv = new ConsoleViewer();
					win2.Content = cv.View;
					win2.Show();
					pumpui(120);
					cv.View.Measure(new System.Windows.Size(1060, 580));
					cv.View.Arrange(new System.Windows.Rect(0, 0, 1060, 580));
					// 提示符停在行末（光标应在 > 后）；CSI ?25h 开光标
					var banner2 =
						"\x1b[2J\x1b[m\x1b[H\x1b[?25h" +
						"Microsoft Windows [版本 10.0.19045.6466]\r\n" +
						"(c) Microsoft Corporation。保留所有权利。\r\n" +
						"\r\n" +
						@"D:\VS_Projects\学习\leetcode>";
					cv.FeedVtForTest(Encoding.UTF8.GetBytes(banner2));
					pumpui(80);
					// 模拟 shell 回显字母（验证光标跟在字后，而非飘到右侧）
					cv.FeedVtForTest(Encoding.UTF8.GetBytes("abcdef"));
					pumpui(60);
					cv.PrepareImeFocusForTest();
					pumpui(120);
					var fullPng = cv.CaptureFullPngForTest();
					var termPng = cv.CapturePngForTest();
					var afterType = cv.DumpScreenTextForTest() ?? "";
					try {
						var local = Path.Combine(
							Path.GetDirectoryName(typeof(SelfTest).Assembly.Location) ?? ".",
							"..", "..", "..", "..", "tmp");
						local = Path.GetFullPath(local);
						Directory.CreateDirectory(local);
						if (fullPng != null)
							File.WriteAllBytes(Path.Combine(local, "console_input_full.png"), fullPng);
						if (termPng != null)
							File.WriteAllBytes(Path.Combine(local, "console_input_term.png"), termPng);
						File.WriteAllText(Path.Combine(local, "console_input.txt"), afterType, new UTF8Encoding(false));
						info("shot.full=" + Path.Combine(local, "console_input_full.png"));
						info("shot.term=" + Path.Combine(local, "console_input_term.png"));
						info("shot.afterType has abcdef=" + (afterType.IndexOf("abcdef", StringComparison.Ordinal) >= 0));
					} catch (Exception ex3) {
						info("shot.full fail " + ex3.Message);
					}
					try { cv.Dispose(); } catch { /* ignore */ }
					try { win2.Close(); } catch { /* ignore */ }
				} catch (Exception ex) {
					shotEx = ex;
				}
				try { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch { /* ignore */ }
			});
			thrShot.SetApartmentState(System.Threading.ApartmentState.STA);
			thrShot.Start();
			if (!thrShot.Join(45000))
				bad("shot.banner", "timeout");
			else if (shotEx != null)
				bad("shot.banner", shotEx.ToString());
			else {
				info("shot.path=" + shotPath);
				info("shot.screen=" + trunc(visible(screenTxt ?? ""), 200));
				info("shot.cells=" + trunc(visible(cellsDbg ?? ""), 300));
				if (screenTxt == null || screenTxt.IndexOf("10.0.19045", StringComparison.Ordinal) < 0)
					bad("shot.version", "version digits missing in buffer");
				else
					ok("shot.version");
				if (screenTxt == null || screenTxt.IndexOf("leetcode", StringComparison.Ordinal) < 0)
					bad("shot.path", "leetcode path missing");
				else
					ok("shot.path");
				if (screenTxt != null && screenTxt.IndexOf("版本", StringComparison.Ordinal) >= 0)
					ok("shot.cjk");
				else
					bad("shot.cjk", "版本 missing");
			}
		} catch (Exception ex) {
			bad("shot.unit", ex.ToString());
		}

		// 4) ConsoleViewer 模拟主窗按键：dir\r 必须出现在屏幕
		try {
			Exception keyEx = null;
			string keyScreen = null;
			string keyStats = null;
			var thrKey = new System.Threading.Thread(() => {
				try {
					var win = new System.Windows.Window {
						Title = "selftest-console-keys",
						Width = 900,
						Height = 500,
						WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
						Left = -32000,
						Top = -32000,
						ShowInTaskbar = false,
					};
					var cv = new ConsoleViewer();
					cv.PreferredWorkDir = Path.GetTempPath();
					win.Content = cv.View;
					win.Show();
					pumpui(100);
					cv.Load("console:cmd");
					pumpui(600);
					// 等提示符
					var t0 = Environment.TickCount;
					while (Environment.TickCount - t0 < 4000) {
						pumpui(80);
						var s0 = cv.DumpScreenTextForTest();
						if (s0 != null && s0.IndexOf('>') >= 0) break;
					}
					info("key.stats0=" + cv.DebugSessionStats());
					// 模拟主窗：逐键 HandleKeyDown（dir + Enter）
					foreach (var k in new[] {
						System.Windows.Input.Key.D,
						System.Windows.Input.Key.I,
						System.Windows.Input.Key.R,
						System.Windows.Input.Key.Return,
					}) {
						cv.TryHandleKey(k, System.Windows.Input.ModifierKeys.None);
						pumpui(40);
					}
					// 再测 TextInput ASCII 路径（中文输入法场景）
					cv.TryHandleText("echo ");
					cv.TryHandleText("KEYPATH_OK");
					cv.TryHandleKey(System.Windows.Input.Key.Return, System.Windows.Input.ModifierKeys.None);
					var t1 = Environment.TickCount;
					while (Environment.TickCount - t1 < 5000) {
						pumpui(80);
						keyScreen = cv.DumpScreenTextForTest();
						if (keyScreen != null && keyScreen.IndexOf("KEYPATH_OK", StringComparison.Ordinal) >= 0)
							break;
					}
					keyStats = cv.DebugSessionStats();
					info("key.stats1=" + keyStats);
					info("key.screen=" + trunc(visible(keyScreen ?? ""), 240));
					try { cv.Dispose(); } catch { /* ignore */ }
					try { win.Close(); } catch { /* ignore */ }
				} catch (Exception ex) {
					keyEx = ex;
				}
				try { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch { /* ignore */ }
			});
			thrKey.SetApartmentState(System.Threading.ApartmentState.STA);
			thrKey.Start();
			if (!thrKey.Join(40000))
				bad("key.e2e", "timeout");
			else if (keyEx != null)
				bad("key.e2e", keyEx.ToString());
			else {
				if (keyScreen != null && keyScreen.IndexOf("KEYPATH_OK", StringComparison.Ordinal) >= 0)
					ok("key.textinput");
				else
					bad("key.textinput", "KEYPATH_OK missing");
				// dir 列表或至少出现 dir 回显
				if (keyScreen != null && (keyScreen.IndexOf("dir", StringComparison.OrdinalIgnoreCase) >= 0
					|| keyScreen.IndexOf("<DIR>", StringComparison.OrdinalIgnoreCase) >= 0))
					ok("key.dir");
				else
					bad("key.dir", "dir not in screen");
			}
		} catch (Exception ex) {
			bad("key.unit", ex.ToString());
		}

		// 5) WPF：TerminalControl 接 ConPTY（不经主窗快捷键），验证显示缓冲
		Exception uiEx = null;
		var uiPass = new System.Collections.Generic.List<string>();
		var uiFail = new System.Collections.Generic.List<string>();
		var thr = new System.Threading.Thread(() => {
			try {
				var win = new System.Windows.Window {
					Title = "selftest-console",
					Width = 800,
					Height = 480,
					WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
					Left = -32000,
					Top = -32000,
					ShowInTaskbar = false,
				};
				var term = new TerminalControl();
				win.Content = term;
				win.Show();
				// 强制布局出行列
				term.Measure(new System.Windows.Size(760, 400));
				term.Arrange(new System.Windows.Rect(0, 0, 760, 400));
				term.Reset();
				pumpui(100);

				using (var s = new ConPtySession()) {
					s.DataReceived += data => {
						try {
							// 必须在 UI 线程 Feed
							win.Dispatcher.BeginInvoke(new Action(() => {
								try { term.Feed(data); } catch { /* ignore */ }
							}));
						} catch { /* ignore */ }
					};
					// 大窗口启动：接近用户 162x43
					var cols = Math.Max(80, term.ViewCols);
					var rows = Math.Max(20, term.ViewRows);
					s.Start("cmd.exe", null, @"D:\VS_Projects\学习\leetcode", cols, rows);
					info("wpf.pid=" + s.ProcessId + " term=" + cols + "x" + rows + " view=" + term.ViewCols + "x" + term.ViewRows);

					var tWait = Environment.TickCount;
					string screen = "";
					while (Environment.TickCount - tWait < 5000) {
						pumpui(80);
						screen = term.DumpScreenText();
						if (screen != null && (screen.IndexOf('>') >= 0 || screen.Trim().Length > 8))
							break;
					}
					info("wpf.screen1=" + trunc(visible(screen ?? ""), 200));
					info("wpf.cells1=" + trunc(visible(term.DumpCellsDebug(4)), 280));
					info("wpf.stats read=" + s.BytesRead + " chunks=" + s.ReadChunks);
					// 截真实 ConPTY 首屏
					try {
						term.InvalidateVisual();
						pumpui(80);
						var png = term.CapturePng();
						var dir = Path.Combine(Path.GetTempPath(), "DocviewWPF_selftest");
						Directory.CreateDirectory(dir);
						var livePng = Path.Combine(dir, "console_live.png");
						if (png != null) File.WriteAllBytes(livePng, png);
						var local = Path.Combine(
							Path.GetDirectoryName(typeof(SelfTest).Assembly.Location) ?? ".",
							"..", "..", "..", "..", "tmp");
						local = Path.GetFullPath(local);
						Directory.CreateDirectory(local);
						if (png != null) File.WriteAllBytes(Path.Combine(local, "console_live.png"), png);
						File.WriteAllText(Path.Combine(local, "console_live.txt"),
							(screen ?? "") + "\n---cells---\n" + term.DumpCellsDebug(5),
							new UTF8Encoding(false));
						info("wpf.liveShot=" + Path.Combine(local, "console_live.png"));
					} catch (Exception exs) {
						info("wpf.liveShot fail " + exs.Message);
					}
					if (string.IsNullOrWhiteSpace(screen) || screen.Trim().Length < 2)
						uiFail.Add("wpf.display empty");
					else
						uiPass.Add("wpf.display");
					// 版本号不得被宽字符逻辑吃掉
					if (screen != null && screen.IndexOf("10.0.", StringComparison.Ordinal) < 0
						&& screen.IndexOf("Microsoft", StringComparison.Ordinal) >= 0)
						uiFail.Add("wpf.version digits missing after CJK");
					else if (screen != null && screen.IndexOf("Microsoft", StringComparison.Ordinal) >= 0)
						uiPass.Add("wpf.version");

					var marker = "DVWPF_UI_" + Guid.NewGuid().ToString("N").Substring(0, 8);
					s.WriteSync(Encoding.UTF8.GetBytes("echo " + marker + "\r\n"));
					term.Output += b => { try { s.Write(b); } catch { /* ignore */ } };

					var found = false;
					var t2 = Environment.TickCount;
					while (Environment.TickCount - t2 < 6000) {
						pumpui(80);
						screen = term.DumpScreenText();
						if (screen != null && screen.IndexOf(marker, StringComparison.Ordinal) >= 0) {
							found = true;
							break;
						}
					}
					info("wpf.screen2=" + trunc(visible(screen ?? ""), 240));
					info("wpf.stats2 read=" + s.BytesRead + " write=" + s.BytesWritten);
					if (!found) uiFail.Add("wpf.echo marker missing " + marker);
					else uiPass.Add("wpf.echo");
				}
				try { term.DisposeResources(); } catch { /* ignore */ }
				try { win.Close(); } catch { /* ignore */ }
			} catch (Exception ex) {
				uiEx = ex;
			}
			try { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch { /* ignore */ }
		});
		thr.SetApartmentState(System.Threading.ApartmentState.STA);
		thr.Start();
		if (!thr.Join(45000)) {
			bad("wpf.e2e", "timeout 45s");
		} else if (uiEx != null) {
			bad("wpf.e2e", uiEx.ToString());
		} else {
			foreach (var p in uiPass) ok(p);
			foreach (var f in uiFail) {
				var sp = f.IndexOf(' ');
				if (sp > 0) bad(f.Substring(0, sp), f.Substring(sp + 1));
				else bad(f, "");
			}
		}

		line(fail == 0 ? "SELFTEST OK" : "SELFTEST FAILED count=" + fail);
		try { fileLog?.Dispose(); } catch { /* ignore */ }
		return fail;
	}

	[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
	static extern bool FreeConsole();

	static string visible(string s) {
		if (s == null) return "";
		var sb = new StringBuilder(s.Length);
		foreach (var ch in s) {
			if (ch == '\r') sb.Append("\\r");
			else if (ch == '\n') sb.Append("\\n");
			else if (ch == '\t') sb.Append("\\t");
			else if (ch == '\x1b') sb.Append("\\e");
			else if (ch < 32) sb.Append($"\\x{(int)ch:X2}");
			else sb.Append(ch);
		}
		return sb.ToString();
	}

	static string trunc(string s, int n) {
		if (s == null) return "";
		return s.Length <= n ? s : s.Substring(0, n) + "…";
	}
}
