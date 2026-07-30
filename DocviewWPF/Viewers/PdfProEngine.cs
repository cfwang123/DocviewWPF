using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;

namespace DocviewWPF;

/// <summary>页对象类型（与 pdfium FPDF_PAGEOBJ_* 对齐）。</summary>
enum PdfProObjType {
	Unknown = 0,
	Text = 1,
	Path = 2,
	Image = 3,
	Form = 5,
}

/// <summary>
/// 页内对象快照（PDF 用户空间：原点左下，Y 向上，单位 pt）。
/// 界面显示时转换为左上原点。
/// </summary>
sealed class PdfProObject {
	public int Page;
	public int Index;
	public PdfProObjType Type;
	public float Left, Bottom, Right, Top;
	public string Text;
	public float FontSize;
	public MediaColor FillColor = MediaColor.FromRgb(0, 0, 0);
	public bool HasFill;
	public bool MarkedDelete;
	/// <summary>文本来自字符级 Unicode（可读）；false 表示可能乱码或空。</summary>
	public bool TextReadable;
	/// <summary>PDF 字体名（BaseFont / Family，可能带子集前缀 ABCDEF+）。</summary>
	public string FontName;
	/// <summary>文字基线原点（PDF 用户空间，来自矩阵 e,f）。</summary>
	public float BaselineX, BaselineY;
	public bool HasBaseline;
	/// <summary>累计平移（PDF 坐标）。</summary>
	public double Tx, Ty;
	public Guid UiId = Guid.NewGuid();

	public double Width => Math.Max(0.5, Right - Left);
	public double Height => Math.Max(0.5, Top - Bottom);

	/// <summary>界面左上系：页高 pageHpt。</summary>
	public void ToUi(double pageHpt, out double x, out double y, out double w, out double h) {
		x = Left + Tx;
		y = pageHpt - (Top + Ty);
		w = Width;
		h = Height;
	}

	public PdfProObject CloneMeta() {
		return new PdfProObject {
			Page = Page, Index = Index, Type = Type,
			Left = Left, Bottom = Bottom, Right = Right, Top = Top,
			Text = Text, FontSize = FontSize, TextReadable = TextReadable,
			FontName = FontName, BaselineX = BaselineX, BaselineY = BaselineY, HasBaseline = HasBaseline,
			FillColor = FillColor, HasFill = HasFill,
			Tx = Tx, Ty = Ty, UiId = UiId,
		};
	}
}

/// <summary>
/// Acrobat 向矢量编辑引擎：基于 pdfium 页对象增删改 + GenerateContent + SaveAsCopy。
/// 真正改 PDF 对象树并写出矢量 PDF（非整页栅格化）。支持撤销快照。
/// </summary>
sealed class PdfProEngine : IDisposable {
	const int MAX_UNDO = 16;
	const long MAX_UNDO_BYTES = 40L * 1024 * 1024; // 单份快照上限，防止大 PDF 爆内存

	IntPtr doc;
	byte[] sourceBytes;
	bool dirty;
	bool disposed;
	readonly Dictionary<int, IntPtr> openPages = new();
	readonly Dictionary<int, List<PdfProObject>> pageObjs = new();
	// 系统字体缓存 path -> font handle（随 doc 生命周期）
	readonly Dictionary<string, IntPtr> loadedFonts = new(StringComparer.OrdinalIgnoreCase);
	// 撤销：文档字节快照
	readonly List<byte[]> undoStack = new();
	readonly List<byte[]> redoStack = new();
	// 防止 WriteBlock 被 GC
	PdfiumNative.FPDF_WriteBlock writeCb;
	GCHandle writePin;

	public int PageCount { get; private set; }
	public System.Drawing.SizeF[] PageSizesPt { get; private set; }
	public bool IsDirty => dirty;
	public bool CanUndo => undoStack.Count > 0;
	public bool CanRedo => redoStack.Count > 0;
	public IntPtr Document => doc;

	public static PdfProEngine Open(string path) {
		var bytes = DocFileIo.ReadAllBytesShared(path) ?? File.ReadAllBytes(path);
		return Open(bytes);
	}

	/// <summary>
	/// 命令行自测改字路径（避免 UI 干扰）。返回 null 表示成功，否则错误信息。
	/// </summary>
	public static string SelfTestReplace(string path) {
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			return "文件不存在: " + path;
		try {
			using var eng = Open(path);
			var list = eng.ListObjects(0, true);
			PdfProObject target = null;
			// 优先找含「装车」的文字
			foreach (var o in list) {
				if (o.Type != PdfProObjType.Text || o.MarkedDelete) continue;
				if (!string.IsNullOrEmpty(o.Text) && o.Text.IndexOf("装车", StringComparison.Ordinal) >= 0) {
					target = o;
					break;
				}
			}
			if (target == null) {
				foreach (var o in list) {
					if (o.Type == PdfProObjType.Text && o.Width > 40 && o.Height > 8) {
						target = o;
						break;
					}
				}
			}
			if (target == null) return "未找到可改文字对象";
			DocLog.Info($"SelfTest target idx={target.Index} text={target.Text} font={target.FontName}");
			var neu = eng.ReplaceText(target, (target.Text ?? "测试") + "X", "华文中宋");
			if (neu == null) return "ReplaceText 返回 null";
			// 渲染验证
			var bmp = eng.Render(0, 400, 560, 96);
			if (bmp == null) return "Render 失败";
			// 二次改
			var list2 = eng.ListObjects(0, true);
			PdfProObject t2 = null;
			foreach (var o in list2) {
				if (o.Type == PdfProObjType.Text && !string.IsNullOrEmpty(o.Text) && o.Text.EndsWith("X", StringComparison.Ordinal)) {
					t2 = o;
					break;
				}
			}
			if (t2 != null) {
				var n2 = eng.ReplaceText(t2, "二次改字OK", "华文中宋");
				if (n2 == null) return "二次 ReplaceText 失败";
				eng.Render(0, 400, 560, 96);
			}
			// 保存到内存
			var saved = eng.SaveToBytes();
			if (saved == null || saved.Length < 100) return "SaveToBytes 失败";
			DocLog.Info($"SelfTestReplace ok saved={saved.Length}");
			return null;
		} catch (Exception ex) {
			DocLog.Error("SelfTestReplace", ex);
			return ex.GetType().Name + ": " + ex.Message;
		}
	}

	public static PdfProEngine Open(byte[] pdfBytes) {
		if (pdfBytes == null || pdfBytes.Length == 0)
			throw new ArgumentException("PDF 为空");
		var eng = new PdfProEngine();
		PdfIo.WithLock(() => {
			try { PdfiumNative.FPDF_InitLibrary(); } catch { /* already */ }
			eng.doc = PdfiumNative.FPDF_LoadMemDocument(pdfBytes, pdfBytes.Length, null);
			if (eng.doc == IntPtr.Zero)
				throw new InvalidOperationException("无法打开 PDF 文档");
			eng.sourceBytes = pdfBytes;
			eng.refreshPageMeta();
		});
		DocLog.Info($"PdfProEngine open pages={eng.PageCount} bytes={pdfBytes.Length}");
		return eng;
	}

	void refreshPageMeta() {
		PageCount = PdfiumNative.FPDF_GetPageCount(doc);
		PageSizesPt = new System.Drawing.SizeF[Math.Max(0, PageCount)];
		for (var i = 0; i < PageCount; i++) {
			PdfiumNative.FPDF_GetPageSizeByIndex(doc, i, out var w, out var h);
			PageSizesPt[i] = new System.Drawing.SizeF((float)Math.Max(1, w), (float)Math.Max(1, h));
		}
	}

	public IntPtr GetPage(int pageIndex) {
		throwif();
		if (pageIndex < 0 || pageIndex >= PageCount)
			throw new ArgumentOutOfRangeException(nameof(pageIndex));
		lock (PdfIo.Gate) {
			if (openPages.TryGetValue(pageIndex, out var p) && p != IntPtr.Zero)
				return p;
			p = PdfiumNative.FPDF_LoadPage(doc, pageIndex);
			if (p == IntPtr.Zero)
				throw new InvalidOperationException($"无法加载第 {pageIndex + 1} 页");
			openPages[pageIndex] = p;
			return p;
		}
	}

	public void ClosePage(int pageIndex) {
		lock (PdfIo.Gate) {
			if (!openPages.TryGetValue(pageIndex, out var p) || p == IntPtr.Zero) return;
			try { PdfiumNative.FPDF_ClosePage(p); } catch { /* ignore */ }
			openPages.Remove(pageIndex);
		}
	}

	void closeAllPages() {
		foreach (var kv in openPages) {
			if (kv.Value != IntPtr.Zero)
				try { PdfiumNative.FPDF_ClosePage(kv.Value); } catch { /* ignore */ }
		}
		openPages.Clear();
	}

	void closeFonts() {
		foreach (var kv in loadedFonts) {
			if (kv.Value != IntPtr.Zero)
				try { PdfiumNative.FPDFFont_Close(kv.Value); } catch { /* ignore */ }
		}
		loadedFonts.Clear();
	}

	public List<PdfProObject> ListObjects(int pageIndex, bool forceReload = false) {
		throwif();
		if (!forceReload && pageObjs.TryGetValue(pageIndex, out var cached))
			return cached;
		var list = new List<PdfProObject>();
		var textObjs = new List<PdfProObject>();
		lock (PdfIo.Gate) {
			var page = GetPage(pageIndex);
			var n = PdfiumNative.FPDFPage_CountObjects(page);
			IntPtr textPage = IntPtr.Zero;
			try {
				// 勿用 FPDFTextObj_GetFontSize / 勿依赖 FPDFTextObj_GetText（CID 中文常乱码且可能 AV）
				try { textPage = PdfiumNative.FPDFText_LoadPage(page); } catch { textPage = IntPtr.Zero; }
				for (var i = 0; i < n; i++) {
					try {
						var obj = PdfiumNative.FPDFPage_GetObject(page, i);
						if (obj == IntPtr.Zero) continue;
						var type = PdfiumNative.FPDFPageObj_GetType(obj);
						if (type != PdfiumNative.FPDF_PAGEOBJ_TEXT
							&& type != PdfiumNative.FPDF_PAGEOBJ_IMAGE
							&& type != PdfiumNative.FPDF_PAGEOBJ_PATH)
							continue;
						if (PdfiumNative.FPDFPageObj_GetBounds(obj, out var l, out var b, out var r, out var t) == 0)
							continue;
						var po = new PdfProObject {
							Page = pageIndex,
							Index = i,
							Type = (PdfProObjType)type,
							Left = l, Bottom = b, Right = r, Top = t,
						};
						var h = t - b;
						if (type == PdfiumNative.FPDF_PAGEOBJ_TEXT || type == PdfiumNative.FPDF_PAGEOBJ_PATH) {
							try {
								if (PdfiumNative.FPDFPageObj_GetFillColor(obj, out var fr, out var fg, out var fb, out var fa) != 0) {
									po.FillColor = MediaColor.FromArgb(
										clampByte(fa), clampByte(fr), clampByte(fg), clampByte(fb));
									po.HasFill = true;
								}
							} catch { /* ignore */ }
						}
						if (type == PdfiumNative.FPDF_PAGEOBJ_TEXT) {
							po.FontSize = estimateFontSize(obj, h);
							captureTextStyle(obj, po);
							textObjs.Add(po);
						}
						list.Add(po);
					} catch (Exception ex) {
						DocLog.Warn($"ListObjects skip i={i}: {ex.Message}");
					}
				}
				// 用字符级 Unicode 拼到各文字对象（与主窗选字同一路径，CID 中文可读）
				if (textPage != IntPtr.Zero && textObjs.Count > 0)
					fillTextFromChars(textPage, textObjs);
			} finally {
				if (textPage != IntPtr.Zero)
					try { PdfiumNative.FPDFText_ClosePage(textPage); } catch { /* ignore */ }
			}
		}
		pageObjs[pageIndex] = list;
		DocLog.Info($"ListObjects page={pageIndex} count={list.Count} text={textObjs.Count}");
		return list;
	}

	/// <summary>
	/// 将 FPDFText_GetUnicode 字符按包围盒归属到 text 对象。
	/// 每个字符只归属面积最小的包含对象，避免重叠重复。
	/// </summary>
	static void fillTextFromChars(IntPtr textPage, List<PdfProObject> textObjs) {
		int nchars;
		try { nchars = PdfiumNative.FPDFText_CountChars(textPage); }
		catch { return; }
		if (nchars <= 0) return;

		// 每个对象收集 (charIndex, ch)
		var buckets = new List<(int idx, char ch)>[textObjs.Count];
		for (var i = 0; i < textObjs.Count; i++)
			buckets[i] = new List<(int, char)>();

		for (var ci = 0; ci < nchars; ci++) {
			uint u;
			try { u = PdfiumNative.FPDFText_GetUnicode(textPage, ci); }
			catch { continue; }
			if (u == 0 || u == 0xFFFF) continue;
			if (u > 0xFFFF) continue; // 本编辑器按 BMP 处理
			var ch = (char)u;
			if (char.IsControl(ch) && ch != '\t' && ch != '\n' && ch != '\r') continue;

			double left, right, bottom, top;
			try {
				PdfiumNative.FPDFText_GetCharBox(textPage, ci, out left, out right, out bottom, out top);
			} catch { continue; }
			var cx = (left + right) * 0.5;
			var cy = (bottom + top) * 0.5;

			var best = -1;
			var bestArea = double.MaxValue;
			for (var ti = 0; ti < textObjs.Count; ti++) {
				var o = textObjs[ti];
				// 略放宽命中，避免字盒贴边漏字
				var pad = Math.Max(0.5, Math.Min(o.Width, o.Height) * 0.08);
				if (cx < o.Left - pad || cx > o.Right + pad) continue;
				if (cy < o.Bottom - pad || cy > o.Top + pad) continue;
				var area = o.Width * o.Height;
				if (area < 0.01) area = 0.01;
				if (area < bestArea) {
					bestArea = area;
					best = ti;
				}
			}
			if (best >= 0)
				buckets[best].Add((ci, ch));
		}

		for (var ti = 0; ti < textObjs.Count; ti++) {
			var hits = buckets[ti];
			if (hits.Count == 0) {
				textObjs[ti].Text = "";
				textObjs[ti].TextReadable = false;
				continue;
			}
			hits.Sort((a, b) => a.idx.CompareTo(b.idx));
			var sb = new StringBuilder(hits.Count);
			foreach (var h in hits)
				sb.Append(h.ch);
			var s = sb.ToString();
			textObjs[ti].Text = s;
			textObjs[ti].TextReadable = looksReadable(s);
		}
	}

	/// <summary>粗略判断是否为可读文本（非纯控制符/替换符乱码）。</summary>
	static bool looksReadable(string s) {
		if (string.IsNullOrEmpty(s)) return false;
		var good = 0;
		var bad = 0;
		foreach (var c in s) {
			if (c == '\uFFFD' || c == '\0') { bad++; continue; }
			if (char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsSymbol(c)
				|| char.IsWhiteSpace(c) || c >= 0x4E00) // CJK 等
				good++;
			else if (c < 0x20) bad++;
			else good++;
		}
		return good > 0 && good >= bad;
	}

	static byte clampByte(uint v) => (byte)(v > 255 ? 255 : v);

	/// <summary>
	/// 估算字号。勿用 FPDFTextObj_GetFontSize（部分 PDF 会 AV）。
	/// 中文 PDF 常 matrix≈1，字号取字盒高/字宽，禁止连乘 0.92 导致越改越小。
	/// </summary>
	static float estimateFontSize(IntPtr obj, float boundsH) {
		double matrixS = 0;
		double boundsW = 0;
		try {
			if (PdfiumNative.FPDFPageObj_GetMatrix(obj, out var m) != 0) {
				var sx = Math.Sqrt(m.a * m.a + m.b * m.b);
				var sy = Math.Sqrt(m.c * m.c + m.d * m.d);
				matrixS = Math.Max(sx, sy);
			}
			if (PdfiumNative.FPDFPageObj_GetBounds(obj, out var l, out var b, out var r, out var t) != 0) {
				boundsW = Math.Max(0, r - l);
				if (boundsH < 0.5) boundsH = (float)Math.Max(0, t - b);
			}
		} catch { /* ignore */ }
		return pickFontSize(matrixS, boundsH, boundsW, null);
	}

	/// <summary>
	/// 综合矩阵、字盒高/宽、字符数选字号。
	/// 中文近似全角：字号 ≈ max(字高, 总宽/字数)。
	/// </summary>
	static float pickFontSize(double matrixS, double boundsH, double boundsW = 0, string text = null) {
		var bh = boundsH;
		var bw = boundsW;
		var n = countVisualChars(text);
		double fromH = bh > 2 && bh < 500 ? bh : 0;
		double fromW = (n > 0 && bw > 2) ? bw / n : 0;
		double fromM = (matrixS >= 3 && matrixS < 500) ? matrixS : 0;

		// 矩阵≈1 时 fromM 不可信
		if (matrixS > 0 && matrixS < 3) fromM = 0;

		double fs = 0;
		if (fromH > 0 && fromW > 0)
			fs = Math.Max(fromH, fromW); // 标题宁大勿小
		else if (fromH > 0)
			fs = fromH;
		else if (fromW > 0)
			fs = fromW;
		else if (fromM > 0)
			fs = fromM;
		else
			fs = 12;

		// 矩阵尺度若与字盒接近，可参考（标准 Tm）
		if (fromM > 0 && fromH > 0 && Math.Abs(fromM - fromH) / fromH < 0.35)
			fs = Math.Max(fs, fromM);

		if (fs < 6) fs = 12;
		if (fs > 200) fs = 72;
		return (float)fs;
	}

	static int countVisualChars(string text) {
		if (string.IsNullOrEmpty(text)) return 0;
		var n = 0;
		foreach (var c in text) {
			if (char.IsWhiteSpace(c) || c == '\0') continue;
			n++;
		}
		return n;
	}

	/// <summary>读取基线与字体名（不调用 GetFontSize，避免 AV）。</summary>
	static void captureTextStyle(IntPtr obj, PdfProObject po) {
		try {
			if (PdfiumNative.FPDFPageObj_GetMatrix(obj, out var m) != 0) {
				po.BaselineX = m.e;
				po.BaselineY = m.f;
				po.HasBaseline = true;
			}
		} catch { /* ignore */ }
		try {
			var font = PdfiumNative.FPDFTextObj_GetFont(obj);
			if (font == IntPtr.Zero) return;
			// GetFont 返回的句柄由文档持有，勿 Close
			var baseName = readFontName(font, baseName: true);
			var family = readFontName(font, baseName: false);
			po.FontName = !string.IsNullOrEmpty(baseName) ? baseName
				: !string.IsNullOrEmpty(family) ? family : null;
			if (!string.IsNullOrEmpty(po.FontName))
				po.FontName = stripSubsetPrefix(po.FontName);
		} catch { /* ignore */ }
	}

	static string readFontName(IntPtr font, bool baseName) {
		try {
			uint len = baseName
				? PdfiumNative.FPDFFont_GetBaseFontName(font, null, 0)
				: PdfiumNative.FPDFFont_GetFamilyName(font, null, 0);
			if (len <= 1 || len > 512) return null;
			var buf = new byte[len];
			var n = baseName
				? PdfiumNative.FPDFFont_GetBaseFontName(font, buf, len)
				: PdfiumNative.FPDFFont_GetFamilyName(font, buf, len);
			if (n == 0) return null;
			var byteLen = (int)Math.Min(n, (uint)buf.Length);
			while (byteLen > 0 && buf[byteLen - 1] == 0) byteLen--;
			if (byteLen <= 0) return null;
			return decodeFontNameBytes(buf, byteLen);
		} catch { return null; }
	}

	/// <summary>解码字体名：优先 ASCII，再 UTF-8/GBK；乱码返回 null 避免 UI 显示 �。</summary>
	static string decodeFontNameBytes(byte[] buf, int n) {
		if (buf == null || n <= 0) return null;
		// 纯 ASCII（PDF BaseFont 常见：Calibri、SimSun）
		var asciiOk = true;
		for (var i = 0; i < n; i++) {
			var b = buf[i];
			if (b == 0) { n = i; break; }
			if (b < 0x20 || b > 0x7E) { asciiOk = false; break; }
		}
		if (asciiOk && n > 0) {
			var a = Encoding.ASCII.GetString(buf, 0, n).Trim();
			return string.IsNullOrEmpty(a) ? null : a;
		}
		// UTF-8
		try {
			var u = Encoding.UTF8.GetString(buf, 0, n).TrimEnd('\0').Trim();
			if (isDisplayableFontName(u)) return u;
		} catch { /* ignore */ }
		// GB18030（部分国产字体名）
		try {
			var g = Encoding.GetEncoding(936).GetString(buf, 0, n).TrimEnd('\0').Trim();
			if (isDisplayableFontName(g)) return g;
		} catch { /* ignore */ }
		return null;
	}

	/// <summary>是否可安全显示在 UI（无替换符/大量控制字符）。</summary>
	public static bool isDisplayableFontName(string s) {
		if (string.IsNullOrWhiteSpace(s)) return false;
		var good = 0;
		var bad = 0;
		foreach (var c in s) {
			if (c == '\0' || c == '\uFFFD' || char.IsControl(c)) { bad += 2; continue; }
			// 私用区/异常符号
			if (c >= 0xE000 && c <= 0xF8FF) { bad++; continue; }
			if (char.IsLetterOrDigit(c) || c >= 0x4E00 || c == ' ' || c == '-' || c == '_' || c == '+' || c == ',')
				good++;
			else if (c < 128)
				good++;
			else
				bad++;
		}
		return good > 0 && good >= bad;
	}

	/// <summary>去掉 PDF 子集前缀如 "ABCDEF+"。</summary>
	static string stripSubsetPrefix(string name) {
		if (string.IsNullOrEmpty(name)) return name;
		var plus = name.IndexOf('+');
		if (plus > 0 && plus <= 6 && plus < name.Length - 1)
			return name.Substring(plus + 1);
		return name;
	}

	/// <summary>
	/// 读取嵌入字体字节。注意：部分 PDF（如嵌入 Calibri）上
	/// FPDFFont_GetFontData 会原生 AV，已禁用，始终返回 null，改用系统字体。
	/// </summary>
	static byte[] tryGetEmbeddedFontData(IntPtr textObj) {
		// 实测手册 PDF：GetIsEmbedded=1 后 GetFontData 直接 0xC0000005，不可捕获。
		// 故彻底禁用嵌入字体字节复用，ReplaceText 只走系统字体。
		return null;
	}

	public BitmapSource Render(int pageIndex, int pixelW, int pixelH, double dipDpi) {
		throwif();
		lock (PdfIo.Gate) {
			var page = GetPage(pageIndex);
			var bmp = PdfiumNative.FPDFBitmap_Create(pixelW, pixelH, 1);
			if (bmp == IntPtr.Zero) return null;
			try {
				PdfiumNative.FPDFBitmap_FillRect(bmp, 0, 0, pixelW, pixelH, 0xFFFFFFFF);
				PdfiumNative.FPDF_RenderPageBitmap(bmp, page, 0, 0, pixelW, pixelH, 0,
					PdfiumNative.FPDF_ANNOT | PdfiumNative.FPDF_LCD_TEXT);
				return bitmapToWpf(bmp, dipDpi);
			} finally {
				PdfiumNative.FPDFBitmap_Destroy(bmp);
			}
		}
	}

	static BitmapSource bitmapToWpf(IntPtr bmp, double dipDpi) {
		var w = PdfiumNative.FPDFBitmap_GetWidth(bmp);
		var h = PdfiumNative.FPDFBitmap_GetHeight(bmp);
		var stride = PdfiumNative.FPDFBitmap_GetStride(bmp);
		var buf = PdfiumNative.FPDFBitmap_GetBuffer(bmp);
		if (w < 1 || h < 1 || buf == IntPtr.Zero) return null;
		var src = new byte[stride * h];
		Marshal.Copy(buf, src, 0, src.Length);
		var bgra = new byte[w * h * 4];
		var outStride = w * 4;
		for (var y = 0; y < h; y++) {
			var s0 = y * stride;
			var d0 = y * outStride;
			for (var x = 0; x < w; x++) {
				var si = s0 + x * 4;
				var di = d0 + x * 4;
				if (si + 3 >= src.Length) break;
				bgra[di] = src[si];
				bgra[di + 1] = src[si + 1];
				bgra[di + 2] = src[si + 2];
				bgra[di + 3] = src[si + 3] == 0 ? (byte)0xFF : src[si + 3];
			}
		}
		var wb = new WriteableBitmap(w, h, dipDpi, dipDpi, PixelFormats.Bgra32, null);
		wb.WritePixels(new Int32Rect(0, 0, w, h), bgra, outStride, 0);
		wb.Freeze();
		return wb;
	}

	// ========== 撤销 / 重做（文档快照） ==========

	/// <summary>在变更前调用：压入当前文档状态。</summary>
	void pushUndo() {
		try {
			var bytes = saveToBytesUnlocked();
			if (bytes == null || bytes.Length == 0) return;
			if (bytes.Length > MAX_UNDO_BYTES) {
				DocLog.Warn($"PdfProEngine undo snapshot too large ({bytes.Length}), skip");
				return;
			}
			undoStack.Add(bytes);
			while (undoStack.Count > MAX_UNDO)
				undoStack.RemoveAt(0);
			redoStack.Clear();
		} catch (Exception ex) {
			DocLog.Warn("pushUndo: " + ex.Message);
		}
	}

	public bool Undo() {
		if (undoStack.Count == 0) return false;
		lock (PdfIo.Gate) {
			try {
				var cur = saveToBytesUnlocked();
				if (cur != null && cur.Length > 0 && cur.Length <= MAX_UNDO_BYTES)
					redoStack.Add(cur);
				var prev = undoStack[undoStack.Count - 1];
				undoStack.RemoveAt(undoStack.Count - 1);
				reloadFromBytesUnlocked(prev);
				dirty = true;
				return true;
			} catch (Exception ex) {
				DocLog.Error("Undo", ex);
				return false;
			}
		}
	}

	public bool Redo() {
		if (redoStack.Count == 0) return false;
		lock (PdfIo.Gate) {
			try {
				var cur = saveToBytesUnlocked();
				if (cur != null && cur.Length > 0 && cur.Length <= MAX_UNDO_BYTES)
					undoStack.Add(cur);
				var next = redoStack[redoStack.Count - 1];
				redoStack.RemoveAt(redoStack.Count - 1);
				reloadFromBytesUnlocked(next);
				dirty = true;
				return true;
			} catch (Exception ex) {
				DocLog.Error("Redo", ex);
				return false;
			}
		}
	}

	void reloadFromBytesUnlocked(byte[] bytes) {
		closeFonts();
		closeAllPages();
		pageObjs.Clear();
		if (doc != IntPtr.Zero) {
			try { PdfiumNative.FPDF_CloseDocument(doc); } catch { /* ignore */ }
			doc = IntPtr.Zero;
		}
		doc = PdfiumNative.FPDF_LoadMemDocument(bytes, bytes.Length, null);
		if (doc == IntPtr.Zero)
			throw new InvalidOperationException("撤销/重做后无法重载文档");
		sourceBytes = bytes;
		refreshPageMeta();
	}

	// ========== 对象编辑 ==========

	/// <summary>移动对象（UI 坐标增量：Y 向下 → PDF Y 取反）。</summary>
	public bool MoveObject(PdfProObject po, double uiDxPt, double uiDyPt, bool recordUndo = true) {
		return MoveObjects(po == null ? null : new[] { po }, uiDxPt, uiDyPt, recordUndo) > 0;
	}

	/// <summary>压入撤销快照（拖动手势开始时调用一次）。</summary>
	public void SnapshotForUndo() {
		lock (PdfIo.Gate) {
			pushUndo();
		}
	}

	/// <summary>
	/// 批量移动。recordUndo=false 用于拖动中连续移动（撤销在 SnapshotForUndo 已记）。
	/// </summary>
	public int MoveObjects(IList<PdfProObject> items, double uiDxPt, double uiDyPt, bool recordUndo = true) {
		if (items == null || items.Count == 0) return 0;
		if (Math.Abs(uiDxPt) < 1e-9 && Math.Abs(uiDyPt) < 1e-9) return items.Count;
		var pdfDx = uiDxPt;
		var pdfDy = -uiDyPt;
		var n = 0;
		lock (PdfIo.Gate) {
			if (recordUndo) pushUndo();
			var pages = new HashSet<int>();
			foreach (var po in items) {
				if (po == null || po.MarkedDelete) continue;
				var page = GetPage(po.Page);
				var obj = PdfiumNative.FPDFPage_GetObject(page, po.Index);
				if (obj == IntPtr.Zero) continue;
				PdfiumNative.FPDFPageObj_Transform(obj, 1, 0, 0, 1, pdfDx, pdfDy);
				po.Tx += pdfDx;
				po.Ty += pdfDy;
				po.Left += (float)pdfDx;
				po.Right += (float)pdfDx;
				po.Top += (float)pdfDy;
				po.Bottom += (float)pdfDy;
				if (po.HasBaseline) {
					po.BaselineX += (float)pdfDx;
					po.BaselineY += (float)pdfDy;
				}
				pages.Add(po.Page);
				n++;
			}
			foreach (var p in pages) {
				if (PdfiumNative.FPDFPage_GenerateContent(GetPage(p)) == 0)
					DocLog.Warn("GenerateContent failed after batch move page=" + p);
			}
			if (n > 0) dirty = true;
		}
		return n;
	}

	/// <summary>以对象中心缩放（scale&gt;0）。</summary>
	public bool ScaleObject(PdfProObject po, double scaleX, double scaleY) {
		if (po == null || po.MarkedDelete) return false;
		if (scaleX < 0.05 || scaleY < 0.05 || scaleX > 20 || scaleY > 20) return false;
		var cx = (po.Left + po.Right) / 2.0;
		var cy = (po.Bottom + po.Top) / 2.0;
		lock (PdfIo.Gate) {
			pushUndo();
			var page = GetPage(po.Page);
			var obj = PdfiumNative.FPDFPage_GetObject(page, po.Index);
			if (obj == IntPtr.Zero) return false;
			// T(-c) * S * T(c)
			PdfiumNative.FPDFPageObj_Transform(obj, 1, 0, 0, 1, -cx, -cy);
			PdfiumNative.FPDFPageObj_Transform(obj, scaleX, 0, 0, scaleY, 0, 0);
			PdfiumNative.FPDFPageObj_Transform(obj, 1, 0, 0, 1, cx, cy);
			if (PdfiumNative.FPDFPage_GenerateContent(page) == 0) return false;
			if (PdfiumNative.FPDFPageObj_GetBounds(obj, out var l, out var b, out var r, out var t) != 0) {
				po.Left = l; po.Bottom = b; po.Right = r; po.Top = t;
			}
			dirty = true;
			pageObjs.Remove(po.Page);
		}
		return true;
	}

	/// <summary>绕中心旋转（度，顺时针正）。</summary>
	public bool RotateObject(PdfProObject po, double degrees) {
		if (po == null || po.MarkedDelete) return false;
		var rad = degrees * Math.PI / 180.0;
		var cos = Math.Cos(rad);
		var sin = Math.Sin(rad);
		// PDF 坐标 Y 向上，顺时针 = 数学逆时针取负
		var a = cos;
		var b = -sin;
		var c = sin;
		var d = cos;
		var cx = (po.Left + po.Right) / 2.0;
		var cy = (po.Bottom + po.Top) / 2.0;
		lock (PdfIo.Gate) {
			pushUndo();
			var page = GetPage(po.Page);
			var obj = PdfiumNative.FPDFPage_GetObject(page, po.Index);
			if (obj == IntPtr.Zero) return false;
			PdfiumNative.FPDFPageObj_Transform(obj, 1, 0, 0, 1, -cx, -cy);
			PdfiumNative.FPDFPageObj_Transform(obj, a, b, c, d, 0, 0);
			PdfiumNative.FPDFPageObj_Transform(obj, 1, 0, 0, 1, cx, cy);
			if (PdfiumNative.FPDFPage_GenerateContent(page) == 0) return false;
			if (PdfiumNative.FPDFPageObj_GetBounds(obj, out var l, out var btm, out var r, out var t) != 0) {
				po.Left = l; po.Bottom = btm; po.Right = r; po.Top = t;
			}
			dirty = true;
			pageObjs.Remove(po.Page);
		}
		return true;
	}

	public bool SetFillColor(PdfProObject po, MediaColor color) {
		if (po == null || po.MarkedDelete) return false;
		lock (PdfIo.Gate) {
			pushUndo();
			var page = GetPage(po.Page);
			var obj = PdfiumNative.FPDFPage_GetObject(page, po.Index);
			if (obj == IntPtr.Zero) return false;
			if (PdfiumNative.FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, color.A) == 0)
				return false;
			if (PdfiumNative.FPDFPage_GenerateContent(page) == 0) return false;
			po.FillColor = color;
			po.HasFill = true;
			dirty = true;
		}
		return true;
	}

	/// <summary>
	/// 替换文字内容。不对原 CID/子集字体原地 SetText（易 AV），
	/// 而是删除旧对象 + 尽量用原字体/匹配系统字体重建。
	/// </summary>
	public bool SetText(PdfProObject po, string text) {
		return ReplaceText(po, text) != null;
	}

	/// <summary>
	/// 安全替换文字。系统字体重建；字号按原字盒高/字宽匹配，避免越改越小。
	/// fontSizeOverride：属性面板指定字号（&gt;0 时优先）。
	/// </summary>
	public PdfProObject ReplaceText(PdfProObject po, string text, string fontHint = null, float fontSizeOverride = 0) {
		if (po == null || po.Type != PdfProObjType.Text || po.MarkedDelete) return null;
		text ??= "";
		var pageIndex = po.Page;
		var boundsH = Math.Max(0, po.Top - po.Bottom);
		var boundsW = Math.Max(0, po.Right - po.Left);
		var boxLeft = (double)po.Left;
		var boxBottom = (double)po.Bottom;
		var boxTop = (double)po.Top;
		var color = po.HasFill ? po.FillColor : MediaColor.FromRgb(0, 0, 0);
		var origFontName = po.FontName;
		string usedFontLabel = null;
		var origText = po.Text ?? "";
		// 用原文估算字号（改长文案时仍按原盒，不按新文字字数缩小）
		var fs = pickFontSize(0, boundsH, boundsW, origText);
		if (fontSizeOverride >= 6 && fontSizeOverride <= 200)
			fs = fontSizeOverride;
		double pdfX = boxLeft;
		double pdfY = boxBottom;

		lock (PdfIo.Gate) {
			pushUndo();
			var page = GetPage(pageIndex);
			var old = PdfiumNative.FPDFPage_GetObject(page, po.Index);
			if (old == IntPtr.Zero) {
				DocLog.Warn("ReplaceText: old object missing");
				return null;
			}

			// 删除前：字盒/基线/字体名
			byte[] embedData = null;
			string liveFontName = origFontName;
			try {
				if (PdfiumNative.FPDFPageObj_GetBounds(old, out var ol, out var ob, out var orx, out var ot) != 0) {
					boundsH = Math.Max(0, ot - ob);
					boundsW = Math.Max(0, orx - ol);
					boxLeft = ol;
					boxBottom = ob;
					boxTop = ot;
				}
				double matrixS = 0;
				double me = boxLeft, mf = boxBottom;
				if (PdfiumNative.FPDFPageObj_GetMatrix(old, out var m) != 0) {
					me = m.e;
					mf = m.f;
					var sx = Math.Sqrt(m.a * m.a + m.b * m.b);
					var sy = Math.Sqrt(m.c * m.c + m.d * m.d);
					matrixS = Math.Max(sx, sy);
				}
				// 字号：面板指定 > 字盒推算（用原文长度）> 矩阵
				if (fontSizeOverride < 6 || fontSizeOverride > 200) {
					fs = pickFontSize(matrixS, boundsH, boundsW, origText);
					// 至少贴近原字高（禁止 0.92 连缩）
					if (boundsH > 8) fs = Math.Max(fs, (float)(boundsH * 0.98));
				}

				// 定位：优先矩阵基线；Y 须落在原字盒内，否则用字盒底
				pdfX = me;
				pdfY = mf;
				if (pdfY < boxBottom - 1 || pdfY > boxTop + 1) {
					pdfY = boxBottom;
					pdfX = boxLeft;
					DocLog.Info($"ReplaceText baseline → box ({pdfX:0.#},{pdfY:0.#})");
				}
				// 水平：与原字盒左对齐（避免漂到邻字上）
				if (Math.Abs(pdfX - boxLeft) > boundsW * 0.5 + 8)
					pdfX = boxLeft;

				po.BaselineX = (float)pdfX;
				po.BaselineY = (float)pdfY;
				po.HasBaseline = true;

				try {
					var f = PdfiumNative.FPDFTextObj_GetFont(old);
					if (f != IntPtr.Zero) {
						var bn = stripSubsetPrefix(readFontName(f, true) ?? readFontName(f, false));
						if (!string.IsNullOrEmpty(bn)) liveFontName = bn;
					}
				} catch (Exception ex) {
					DocLog.Warn("GetFont/name: " + ex.Message);
				}
				DocLog.Info($"ReplaceText style fontName={liveFontName} fs={fs:0.##} box={boundsW:0.#}x{boundsH:0.#} bas=({pdfX:0.#},{pdfY:0.#}) chars={countVisualChars(origText)}");
			} catch (Exception ex) {
				DocLog.Warn("ReplaceText capture style: " + ex.Message);
			}

			// 构建字体尝试列表（仅系统/标准字体，禁止 GetFontData 嵌入复用）
			var candidates = new List<FontCandidate>();
			// 用户指定优先（下拉「华文中宋」等）
			if (!string.IsNullOrWhiteSpace(fontHint)) {
				foreach (var sys in mapPdfFontToSystem(fontHint))
					candidates.Add(FontCandidate.FromSystem(sys));
				candidates.Add(FontCandidate.FromSystem(fontHint.Trim()));
				var stdHint = mapStandardFont(fontHint);
				if (stdHint != null)
					candidates.Add(FontCandidate.FromStandard(stdHint));
			}
			// 原 PDF 字体名映射
			foreach (var sys in mapPdfFontToSystem(liveFontName))
				candidates.Add(FontCandidate.FromSystem(sys));
			// 中文内容强制可用中文字体（原字体常是 Calibri 等西文子集）
			if (needsCjkText(text) || needsCjkText(origText)) {
				candidates.Add(FontCandidate.FromSystem("华文中宋"));
				candidates.Add(FontCandidate.FromSystem("STZhongsong"));
				candidates.Add(FontCandidate.FromSystem("SimSun"));
				candidates.Add(FontCandidate.FromSystem("Microsoft YaHei"));
			} else {
				candidates.Add(FontCandidate.FromStandard("Helvetica"));
				candidates.Add(FontCandidate.FromSystem("Arial"));
				candidates.Add(FontCandidate.FromSystem("Calibri"));
			}
			// embedData 保留变量但不再加入候选（GetFontData 会崩）
			_ = embedData;

			// 去重 label
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var uniq = new List<FontCandidate>();
			foreach (var c in candidates) {
				var key = c.Key;
				if (!seen.Add(key)) continue;
				uniq.Add(c);
			}

			if (PdfiumNative.FPDFPage_RemoveObject(page, old) == 0) {
				DocLog.Warn("ReplaceText: RemoveObject failed");
				return null;
			}
			po.MarkedDelete = true;

			IntPtr neu = IntPtr.Zero;
			try {
				Exception last = null;
				foreach (var cand in uniq) {
					try {
						neu = createTextObjWithCandidate(cand, fs);
						if (neu == IntPtr.Zero) continue;
						if (PdfiumNative.FPDFText_SetText(neu, text) == 0) {
							PdfiumNative.FPDFPageObj_Destroy(neu);
							neu = IntPtr.Zero;
							continue;
						}
						// 子集字体可能 SetText=1 但包围盒仍接近 0（隐形字）→ 放弃
						PdfiumNative.FPDFPageObj_Transform(neu, 1, 0, 0, 1, pdfX, pdfY);
						if (PdfiumNative.FPDFPageObj_GetBounds(neu, out var nl, out var nb, out var nr, out var nt) != 0) {
							var nw = nr - nl;
							var nh = nt - nb;
							var expectMin = Math.Max(4, fs * 0.3);
							if (nw < expectMin && nh < expectMin && text.Trim().Length > 0) {
								DocLog.Warn($"ReplaceText reject invisible glyph font={cand.Label} box={nw:0.#}x{nh:0.#}");
								// 移回原点以便 Destroy 干净
								PdfiumNative.FPDFPageObj_Transform(neu, 1, 0, 0, 1, -pdfX, -pdfY);
								PdfiumNative.FPDFPageObj_Destroy(neu);
								neu = IntPtr.Zero;
								continue;
							}
						}
						usedFontLabel = cand.Label;
						break;
					} catch (Exception ex) {
						last = ex;
						if (neu != IntPtr.Zero) {
							try { PdfiumNative.FPDFPageObj_Destroy(neu); } catch { /* ignore */ }
							neu = IntPtr.Zero;
						}
					}
				}
				if (neu == IntPtr.Zero)
					throw last ?? new InvalidOperationException("无法用任何字体创建文字（可能缺字形）");

				PdfiumNative.FPDFPageObj_SetFillColor(neu, color.R, color.G, color.B, color.A);
				// 标题感：略加描边模拟字重（原嵌入字常偏粗）
				if (fs >= 12 && needsCjkText(text)) {
					try {
						PdfiumNative.FPDFPageObj_SetStrokeColor(neu, color.R, color.G, color.B, color.A);
						PdfiumNative.FPDFPageObj_SetStrokeWidth(neu, Math.Max(0.15f, fs * 0.012f));
						PdfiumNative.FPDFTextObj_SetTextRenderMode(neu, 2); // FillStroke
					} catch { /* 可选 */ }
				}
				// 已在上面 Transform 到基线
				PdfiumNative.FPDFPage_InsertObject(page, neu);
				neu = IntPtr.Zero;

				if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
					throw new InvalidOperationException("GenerateContent 失败");

				dirty = true;
				pageObjs.Remove(pageIndex);
				DocLog.Info($"ReplaceText ok font={usedFontLabel} size={fs:0.##} at=({pdfX:0.#},{pdfY:0.#}) boundsH={boundsH:0.#}");
			} catch (Exception ex) {
				if (neu != IntPtr.Zero) {
					try { PdfiumNative.FPDFPageObj_Destroy(neu); } catch { /* ignore */ }
				}
				DocLog.Error("ReplaceText", ex);
				pageObjs.Remove(pageIndex);
				throw;
			}
		}

		var found = findNearest(pageIndex, PdfProObjType.Text, pdfX, pdfY);
		if (found != null) {
			found.Text = text;
			found.TextReadable = true;
			found.FontSize = fs;
			found.FillColor = color;
			found.HasFill = true;
			found.FontName = usedFontLabel ?? origFontName;
			found.BaselineX = (float)pdfX;
			found.BaselineY = (float)pdfY;
			found.HasBaseline = true;
		}
		return found;
	}

	IntPtr createTextObjWithCandidate(FontCandidate cand, float fs) {
		// 不再使用 EmbedData（GetFontData 在部分 PDF 上会原生崩溃）
		if (!string.IsNullOrEmpty(cand.SystemName)) {
			try {
				var h = getOrLoadFont(cand.SystemName);
				if (h != IntPtr.Zero) {
					var obj = PdfiumNative.FPDFPageObj_CreateTextObj(doc, h, fs);
					if (obj != IntPtr.Zero) return obj;
				}
			} catch (Exception ex) {
				DocLog.Warn("createText system " + cand.SystemName + ": " + ex.Message);
			}
		}
		if (!string.IsNullOrEmpty(cand.StandardName)) {
			try {
				var obj = PdfiumNative.FPDFPageObj_NewTextObj(doc, cand.StandardName, fs);
				if (obj != IntPtr.Zero) return obj;
				var h = PdfiumNative.FPDFText_LoadStandardFont(doc, cand.StandardName);
				if (h != IntPtr.Zero) {
					var key = "std:" + cand.StandardName;
					if (!loadedFonts.ContainsKey(key))
						loadedFonts[key] = h;
					return PdfiumNative.FPDFPageObj_CreateTextObj(doc, h, fs);
				}
			} catch (Exception ex) {
				DocLog.Warn("createText std " + cand.StandardName + ": " + ex.Message);
			}
		}
		return IntPtr.Zero;
	}

	/// <summary>PDF 字体名 → 本机系统字体候选（保风格；华文中宋优先于普通宋体）。</summary>
	static IEnumerable<string> mapPdfFontToSystem(string pdfName) {
		if (string.IsNullOrWhiteSpace(pdfName)) yield break;
		var n = stripSubsetPrefix(pdfName.Trim());
		var lower = n.ToLowerInvariant();

		// 直接是系统友好名
		if (n.Equals("Microsoft YaHei", StringComparison.OrdinalIgnoreCase)
			|| n.Equals("微软雅黑", StringComparison.OrdinalIgnoreCase)) {
			yield return "Microsoft YaHei";
			yield break;
		}
		if (n.Contains("华文中宋") || n.Equals("STZhongsong", StringComparison.OrdinalIgnoreCase)
			|| n.Equals("华文中宋", StringComparison.OrdinalIgnoreCase)) {
			yield return "华文中宋";
			yield return "STZhongsong";
			yield break;
		}

		// 华文中宋 / 中宋（必须先于泛 "song" 匹配，否则会误落到 SimSun）
		if (lower.Contains("zhongsong") || lower.Contains("stzhongs") || lower.Contains("zhong-song")
			|| n.Contains("中宋") || lower.Contains("stzhongsong")
			|| (lower.Contains("zhong") && lower.Contains("song"))) {
			yield return "华文中宋";
			yield return "STZhongsong";
			yield return "SimSun";
			yield break;
		}

		// 华文系列其它
		if (lower.Contains("stsong") || n.Contains("华文宋") || lower.Contains("stsong")) {
			yield return "华文宋体";
			yield return "STSong";
			yield return "SimSun";
			yield break;
		}
		if (lower.Contains("stkaiti") || n.Contains("华文楷")) {
			yield return "华文楷体";
			yield return "STKaiti";
			yield return "KaiTi";
			yield break;
		}
		if (lower.Contains("stxihei") || n.Contains("华文细黑")) {
			yield return "华文细黑";
			yield return "STXihei";
			yield return "Microsoft YaHei";
			yield break;
		}

		// 宋体 / 衬线中文
		if (lower.Contains("simsun") || lower.Contains("nsimsun") || lower.Contains("songti")
			|| n.Contains("宋体") || (n.Contains("宋") && !n.Contains("中宋"))
			|| lower.Contains("serif") || lower.Contains("times")
			|| lower.Contains("ming") || lower.Contains("mincho")) {
			// 标题类 PDF 常来自 Word「华文中宋」导出，song 泛匹配时也先试中宋
			if (lower.Contains("song") && !lower.Contains("simsun") && !lower.Contains("nsimsun")) {
				yield return "华文中宋";
				yield return "STZhongsong";
			}
			yield return "SimSun";
			yield return "NSimSun";
			yield return "宋体";
			yield break;
		}
		// 黑体
		if (lower.Contains("simhei") || lower.Contains("heiti") || n.Contains("黑体")
			|| (n.Contains("黑") && !n.Contains("雅黑")) || lower.Contains("gothic")) {
			if (lower.Contains("yahei") || lower.Contains("microsoft")) {
				yield return "Microsoft YaHei";
				yield break;
			}
			yield return "SimHei";
			yield return "黑体";
			yield break;
		}
		// 雅黑
		if (lower.Contains("yahei") || lower.Contains("msyh") || n.Contains("雅黑")) {
			yield return "Microsoft YaHei";
			yield break;
		}
		// 楷体
		if (lower.Contains("kaiti") || (lower.Contains("kai") && !lower.Contains("stkai"))
			|| n.Contains("楷")) {
			yield return "KaiTi";
			yield return "楷体";
			yield break;
		}
		// 仿宋
		if (lower.Contains("fangsong") || n.Contains("仿")) {
			yield return "FangSong";
			yield return "仿宋";
			yield break;
		}
		// 西文
		if (lower.Contains("arial") || lower.Contains("helvetica")) {
			yield return "Arial";
			yield break;
		}
		if (lower.Contains("courier")) {
			yield return "Courier New";
			yield break;
		}
		// 未知中文 PDF 字体：封面标题优先华文中宋（Word 常用），再宋体/雅黑
		if (needsCjkText(n) || looksLikeCjkFontName(n)) {
			yield return "华文中宋";
			yield return "STZhongsong";
			yield return "SimSun";
			yield return "Microsoft YaHei";
		}
	}

	static bool looksLikeCjkFontName(string n) {
		if (string.IsNullOrEmpty(n)) return false;
		foreach (var c in n)
			if (c >= 0x4E00) return true;
		// 常见 CID 字体前缀
		var l = n.ToLowerInvariant();
		return l.Contains("cid") || l.Contains("gb") || l.Contains("cjk")
			|| l.Contains("adobe") || l.StartsWith("fz") || l.Contains("sourcehan");
	}

	static bool needsCjkText(string text) {
		if (string.IsNullOrEmpty(text)) return false;
		foreach (var c in text) {
			if (c >= 0x3000) return true;
		}
		return false;
	}

	struct FontCandidate {
		public byte[] EmbedData;
		public string SystemName;
		public string StandardName;
		public string Label;
		public string Key => Label ?? "";

		public static FontCandidate FromEmbed(byte[] data, string label) => new FontCandidate {
			EmbedData = data, Label = "embed:" + label,
		};
		public static FontCandidate FromSystem(string name) => new FontCandidate {
			SystemName = name, Label = "sys:" + name,
		};
		public static FontCandidate FromStandard(string name) => new FontCandidate {
			StandardName = name, Label = "std:" + name,
		};
	}

	public bool DeleteObject(PdfProObject po) {
		if (po == null || po.MarkedDelete) return false;
		lock (PdfIo.Gate) {
			pushUndo();
			var page = GetPage(po.Page);
			var obj = PdfiumNative.FPDFPage_GetObject(page, po.Index);
			if (obj == IntPtr.Zero) return false;
			if (PdfiumNative.FPDFPage_RemoveObject(page, obj) == 0) {
				DocLog.Warn("RemoveObject failed");
				return false;
			}
			if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
				return false;
			po.MarkedDelete = true;
			dirty = true;
			pageObjs.Remove(po.Page);
		}
		return true;
	}

	/// <summary>获取或加载系统 TrueType 字体（CID 支持中文）。</summary>
	IntPtr getOrLoadFont(string fontHint) {
		// 标准 14 字体
		var std = mapStandardFont(fontHint);
		if (std != null) {
			var key = "std:" + std;
			if (loadedFonts.TryGetValue(key, out var sf) && sf != IntPtr.Zero) return sf;
			sf = PdfiumNative.FPDFText_LoadStandardFont(doc, std);
			if (sf != IntPtr.Zero) {
				loadedFonts[key] = sf;
				return sf;
			}
		}
		// 系统字体文件
		var path = resolveSystemFontPath(fontHint);
		if (path == null || !File.Exists(path)) {
			// 回退微软雅黑 / 宋体
			path = resolveSystemFontPath("Microsoft YaHei")
				?? resolveSystemFontPath("SimSun")
				?? resolveSystemFontPath("Arial");
		}
		if (path == null || !File.Exists(path)) return IntPtr.Zero;
		if (loadedFonts.TryGetValue(path, out var cached) && cached != IntPtr.Zero)
			return cached;
		var data = File.ReadAllBytes(path);
		// cid=1 以支持 CJK
		var font = PdfiumNative.FPDFText_LoadFont(doc, data, (uint)data.Length,
			PdfiumNative.FPDF_FONT_TRUETYPE, 1);
		if (font == IntPtr.Zero) {
			// 非 CID 再试
			font = PdfiumNative.FPDFText_LoadFont(doc, data, (uint)data.Length,
				PdfiumNative.FPDF_FONT_TRUETYPE, 0);
		}
		if (font != IntPtr.Zero)
			loadedFonts[path] = font;
		return font;
	}

	static string mapStandardFont(string hint) {
		if (string.IsNullOrWhiteSpace(hint)) return "Helvetica";
		var h = hint.Trim();
		if (h.Equals("Helvetica", StringComparison.OrdinalIgnoreCase)
			|| h.Equals("Arial", StringComparison.OrdinalIgnoreCase)
			|| h.Equals("sans-serif", StringComparison.OrdinalIgnoreCase))
			return "Helvetica";
		if (h.Equals("Times-Roman", StringComparison.OrdinalIgnoreCase)
			|| h.Equals("Times", StringComparison.OrdinalIgnoreCase)
			|| h.Equals("Times New Roman", StringComparison.OrdinalIgnoreCase)
			|| h.Equals("serif", StringComparison.OrdinalIgnoreCase))
			return "Times-Roman";
		if (h.Equals("Courier", StringComparison.OrdinalIgnoreCase)
			|| h.Equals("Courier New", StringComparison.OrdinalIgnoreCase)
			|| h.Equals("monospace", StringComparison.OrdinalIgnoreCase))
			return "Courier";
		if (h.Equals("Helvetica-Bold", StringComparison.OrdinalIgnoreCase)) return "Helvetica-Bold";
		if (h.Equals("Helvetica-Oblique", StringComparison.OrdinalIgnoreCase)) return "Helvetica-Oblique";
		if (h.Equals("Times-Bold", StringComparison.OrdinalIgnoreCase)) return "Times-Bold";
		if (h.Equals("Times-Italic", StringComparison.OrdinalIgnoreCase)) return "Times-Italic";
		if (h.Equals("Courier-Bold", StringComparison.OrdinalIgnoreCase)) return "Courier-Bold";
		// 含中文名 / CJK 字体 → 不走标准字体
		if (h.IndexOf("YaHei", StringComparison.OrdinalIgnoreCase) >= 0
			|| h.IndexOf("SimSun", StringComparison.OrdinalIgnoreCase) >= 0
			|| h.IndexOf("SimHei", StringComparison.OrdinalIgnoreCase) >= 0
			|| h.IndexOf("KaiTi", StringComparison.OrdinalIgnoreCase) >= 0
			|| h.IndexOf("宋", StringComparison.Ordinal) >= 0
			|| h.IndexOf("黑", StringComparison.Ordinal) >= 0
			|| h.IndexOf("楷", StringComparison.Ordinal) >= 0
			|| h.IndexOf("微软", StringComparison.Ordinal) >= 0)
			return null;
		return null; // 尝试系统字体
	}

	static string resolveSystemFontPath(string name) {
		if (string.IsNullOrWhiteSpace(name)) return null;
		var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
		if (string.IsNullOrEmpty(fonts)) fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
		// 常见映射
		var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
			["Microsoft YaHei"] = new[] { "msyh.ttc", "msyh.ttf", "msyhl.ttc" },
			["微软雅黑"] = new[] { "msyh.ttc", "msyh.ttf" },
			// 华文中宋（Word 标题常用，对应 STZHONGS.TTF）
			["华文中宋"] = new[] { "STZHONGS.TTF", "stzhongs.ttf", "STZHONGS.ttf" },
			["STZhongsong"] = new[] { "STZHONGS.TTF", "stzhongs.ttf" },
			["华文宋体"] = new[] { "STSONG.TTF", "stsong.ttf" },
			["STSong"] = new[] { "STSONG.TTF", "stsong.ttf" },
			["华文楷体"] = new[] { "STKAITI.TTF", "stkaiti.ttf" },
			["STKaiti"] = new[] { "STKAITI.TTF", "stkaiti.ttf" },
			["华文细黑"] = new[] { "STXIHEI.TTF", "stxihei.ttf" },
			["STXihei"] = new[] { "STXIHEI.TTF", "stxihei.ttf" },
			["SimSun"] = new[] { "simsun.ttc", "simsun.ttf", "SIMSUN.TTC" },
			["NSimSun"] = new[] { "nsimsun.ttc", "simsun.ttc", "simsun.ttf" },
			["宋体"] = new[] { "simsun.ttc", "simsun.ttf" },
			["SimHei"] = new[] { "simhei.ttf" },
			["黑体"] = new[] { "simhei.ttf" },
			["KaiTi"] = new[] { "simkai.ttf" },
			["楷体"] = new[] { "simkai.ttf" },
			["FangSong"] = new[] { "simfang.ttf", "SIMFANG.TTF" },
			["仿宋"] = new[] { "simfang.ttf" },
			["Arial"] = new[] { "arial.ttf", "arial.ttc" },
			["Times New Roman"] = new[] { "times.ttf", "times.ttc" },
			["Courier New"] = new[] { "cour.ttf" },
		};
		if (map.TryGetValue(name.Trim(), out var files)) {
			foreach (var f in files) {
				var p = Path.Combine(fonts, f);
				if (File.Exists(p)) return p;
			}
		}
		// 直接当文件名
		var direct = Path.Combine(fonts, name);
		if (File.Exists(direct)) return direct;
		if (File.Exists(name)) return name;
		return null;
	}

	/// <summary>在页上新增文字。支持系统中文字体嵌入。坐标 UI 左上 → PDF。</summary>
	public PdfProObject AddText(int pageIndex, double uiX, double uiY, string text,
		float fontSize = 12, string font = "Helvetica", MediaColor? fill = null) {
		throwif();
		text ??= "";
		var pageH = PageSizesPt[pageIndex].Height;
		var pdfX = (float)uiX;
		var pdfY = (float)(pageH - uiY - fontSize);
		var color = fill ?? MediaColor.FromRgb(0, 0, 0);
		lock (PdfIo.Gate) {
			pushUndo();
			var page = GetPage(pageIndex);
			IntPtr obj = IntPtr.Zero;
			// 优先 LoadFont / CreateTextObj（支持中文）
			var fontHandle = getOrLoadFont(font);
			if (fontHandle != IntPtr.Zero) {
				obj = PdfiumNative.FPDFPageObj_CreateTextObj(doc, fontHandle, fontSize);
			}
			if (obj == IntPtr.Zero) {
				var std = mapStandardFont(font) ?? "Helvetica";
				obj = PdfiumNative.FPDFPageObj_NewTextObj(doc, std, fontSize);
			}
			if (obj == IntPtr.Zero) {
				obj = PdfiumNative.FPDFPageObj_NewTextObj(doc, "Helvetica", fontSize);
			}
			if (obj == IntPtr.Zero) throw new InvalidOperationException("无法创建文字对象");
			if (PdfiumNative.FPDFText_SetText(obj, text) == 0) {
				PdfiumNative.FPDFPageObj_Destroy(obj);
				throw new InvalidOperationException("设置文字失败（可能缺少字形）");
			}
			PdfiumNative.FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, color.A);
			PdfiumNative.FPDFPageObj_Transform(obj, 1, 0, 0, 1, pdfX, pdfY);
			PdfiumNative.FPDFPage_InsertObject(page, obj);
			if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
				throw new InvalidOperationException("GenerateContent 失败");
			dirty = true;
			pageObjs.Remove(pageIndex);
		}
		return findNearest(pageIndex, PdfProObjType.Text, pdfX, pdfY);
	}

	/// <summary>插入图片。ui 坐标左上 pt。</summary>
	public PdfProObject AddImage(int pageIndex, double uiX, double uiY, double uiW, double uiH, BitmapSource src) {
		throwif();
		if (src == null) throw new ArgumentNullException(nameof(src));
		var pageH = PageSizesPt[pageIndex].Height;
		var pdfX = uiX;
		var pdfY = pageH - uiY - uiH;
		var pdfW = uiW;
		var pdfH = uiH;

		var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
		var pw = conv.PixelWidth;
		var ph = conv.PixelHeight;
		var stride = pw * 4;
		var pixels = new byte[stride * ph];
		conv.CopyPixels(pixels, stride, 0);

		lock (PdfIo.Gate) {
			pushUndo();
			var page = GetPage(pageIndex);
			var imgObj = PdfiumNative.FPDFPageObj_NewImageObj(doc);
			if (imgObj == IntPtr.Zero) throw new InvalidOperationException("无法创建图片对象");

			var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
			try {
				var fbmp = PdfiumNative.FPDFBitmap_CreateEx(pw, ph, PdfiumNative.FPDFBitmap_BGRA,
					handle.AddrOfPinnedObject(), stride);
				if (fbmp == IntPtr.Zero) {
					PdfiumNative.FPDFPageObj_Destroy(imgObj);
					throw new InvalidOperationException("无法创建位图");
				}
				try {
					var pages = new[] { page };
					if (PdfiumNative.FPDFImageObj_SetBitmap(pages, 1, imgObj, fbmp) == 0) {
						PdfiumNative.FPDFPageObj_Destroy(imgObj);
						throw new InvalidOperationException("SetBitmap 失败");
					}
				} finally {
					PdfiumNative.FPDFBitmap_Destroy(fbmp);
				}
			} finally {
				handle.Free();
			}

			PdfiumNative.FPDFImageObj_SetMatrix(imgObj, pdfW, 0, 0, pdfH, pdfX, pdfY);
			PdfiumNative.FPDFPage_InsertObject(page, imgObj);
			if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
				throw new InvalidOperationException("GenerateContent 失败");
			dirty = true;
			pageObjs.Remove(pageIndex);
		}
		return findNearest(pageIndex, PdfProObjType.Image, pdfX, pdfY);
	}

	/// <summary>添加填充矩形。UI 左上坐标。</summary>
	public PdfProObject AddRect(int pageIndex, double uiX, double uiY, double uiW, double uiH,
		MediaColor fill, bool stroke = false, MediaColor? strokeColor = null, float strokeWidth = 1f) {
		throwif();
		var pageH = PageSizesPt[pageIndex].Height;
		var left = (float)uiX;
		var bottom = (float)(pageH - uiY - uiH);
		var w = (float)Math.Max(0.5, uiW);
		var h = (float)Math.Max(0.5, uiH);
		lock (PdfIo.Gate) {
			pushUndo();
			var page = GetPage(pageIndex);
			var path = PdfiumNative.FPDFPageObj_CreateNewRect(left, bottom, w, h);
			if (path == IntPtr.Zero) throw new InvalidOperationException("CreateNewRect 失败");
			PdfiumNative.FPDFPageObj_SetFillColor(path, fill.R, fill.G, fill.B, fill.A);
			if (stroke && strokeColor.HasValue) {
				var sc = strokeColor.Value;
				PdfiumNative.FPDFPageObj_SetStrokeColor(path, sc.R, sc.G, sc.B, sc.A);
				PdfiumNative.FPDFPageObj_SetStrokeWidth(path, strokeWidth);
				PdfiumNative.FPDFPath_SetDrawMode(path, PdfiumNative.FPDF_FILLMODE_WINDING, 1);
			} else {
				PdfiumNative.FPDFPath_SetDrawMode(path, PdfiumNative.FPDF_FILLMODE_WINDING, 0);
			}
			PdfiumNative.FPDFPage_InsertObject(page, path);
			if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
				throw new InvalidOperationException("GenerateContent 失败");
			dirty = true;
			pageObjs.Remove(pageIndex);
		}
		return findNearest(pageIndex, PdfProObjType.Path, left, bottom);
	}

	/// <summary>白矩形遮盖（擦除式编辑）。</summary>
	public PdfProObject AddWhiteout(int pageIndex, double uiX, double uiY, double uiW, double uiH) {
		return AddRect(pageIndex, uiX, uiY, uiW, uiH, MediaColor.FromRgb(255, 255, 255));
	}

	/// <summary>复制对象：文字/路径用重放；图片提取位图重插。</summary>
	public PdfProObject DuplicateObject(PdfProObject po, double uiOffsetX = 12, double uiOffsetY = 12) {
		if (po == null || po.MarkedDelete) return null;
		var pageH = PageSizesPt[po.Page].Height;
		po.ToUi(pageH, out var x, out var y, out var w, out var h);
		x += uiOffsetX;
		y += uiOffsetY;
		if (po.Type == PdfProObjType.Text) {
			return AddText(po.Page, x, y, po.Text ?? "Text",
				po.FontSize > 1 ? po.FontSize : 12, "Microsoft YaHei",
				po.HasFill ? po.FillColor : (MediaColor?)null);
		}
		if (po.Type == PdfProObjType.Path) {
			return AddRect(po.Page, x, y, w, h,
				po.HasFill ? po.FillColor : MediaColor.FromRgb(200, 200, 200));
		}
		if (po.Type == PdfProObjType.Image) {
			// 尝试从对象取位图
			BitmapSource bmp = null;
			lock (PdfIo.Gate) {
				var page = GetPage(po.Page);
				var obj = PdfiumNative.FPDFPage_GetObject(page, po.Index);
				if (obj != IntPtr.Zero) {
					IntPtr nb = IntPtr.Zero;
					try {
						nb = PdfiumNative.FPDFImageObj_GetRenderedBitmap(doc, page, obj);
						if (nb == IntPtr.Zero)
							nb = PdfiumNative.FPDFImageObj_GetBitmap(obj);
						if (nb != IntPtr.Zero)
							bmp = bitmapToWpf(nb, 96);
					} finally {
						if (nb != IntPtr.Zero)
							try { PdfiumNative.FPDFBitmap_Destroy(nb); } catch { /* ignore */ }
					}
				}
			}
			if (bmp == null) throw new InvalidOperationException("无法复制图片对象");
			return AddImage(po.Page, x, y, w, h, bmp);
		}
		return null;
	}

	// ========== 页操作 ==========

	public void InsertBlankPage(int atIndex, double widthPt = 595, double heightPt = 842) {
		throwif();
		if (atIndex < 0) atIndex = 0;
		if (atIndex > PageCount) atIndex = PageCount;
		lock (PdfIo.Gate) {
			pushUndo();
			// 插入前关闭所有页（索引会变）
			closeAllPages();
			pageObjs.Clear();
			var page = PdfiumNative.FPDFPage_New(doc, atIndex, widthPt, heightPt);
			if (page == IntPtr.Zero) throw new InvalidOperationException("无法新建页面");
			// FPDFPage_New 已插入并返回句柄，先关掉让我们按需重开
			try { PdfiumNative.FPDF_ClosePage(page); } catch { /* ignore */ }
			refreshPageMeta();
			dirty = true;
		}
	}

	public void DeletePage(int pageIndex) {
		throwif();
		if (PageCount <= 1) throw new InvalidOperationException("至少保留一页");
		if (pageIndex < 0 || pageIndex >= PageCount)
			throw new ArgumentOutOfRangeException(nameof(pageIndex));
		lock (PdfIo.Gate) {
			pushUndo();
			closeAllPages();
			pageObjs.Clear();
			PdfiumNative.FPDFPage_Delete(doc, pageIndex);
			refreshPageMeta();
			dirty = true;
		}
	}

	/// <summary>旋转页面 0/1/2/3 → 0°/90°/180°/270°。</summary>
	public void RotatePage(int pageIndex, int deltaQuarterTurns) {
		throwif();
		lock (PdfIo.Gate) {
			pushUndo();
			// 关闭后重开以应用 rotation 元数据
			ClosePage(pageIndex);
			var page = GetPage(pageIndex);
			var cur = PdfiumNative.FPDFPage_GetRotation(page);
			var next = ((cur + deltaQuarterTurns) % 4 + 4) % 4;
			PdfiumNative.FPDFPage_SetRotation(page, next);
			// 旋转是页属性，不一定需要 GenerateContent，但刷新尺寸
			ClosePage(pageIndex);
			refreshPageMeta();
			// 注意：GetPageSizeByIndex 可能已含 rotation
			dirty = true;
			pageObjs.Remove(pageIndex);
		}
	}

	// ========== 保存 ==========

	public byte[] SaveToBytes() {
		throwif();
		lock (PdfIo.Gate) {
			return saveToBytesUnlocked();
		}
	}

	/// <summary>
	/// 序列化当前文档。注意：对 LoadMemDocument 打开的已有 PDF，直接 SaveAsCopy
	/// 常会丢掉 GenerateContent 后的新对象；先 ImportPages 到新文档再保存可保留编辑。
	/// </summary>
	byte[] saveToBytesUnlocked() {
		foreach (var kv in openPages) {
			if (kv.Value != IntPtr.Zero)
				PdfiumNative.FPDFPage_GenerateContent(kv.Value);
		}

		// 优先：新文档 + ImportPages（可靠保留对象编辑）
		var dest = PdfiumNative.FPDF_CreateNewDocument();
		if (dest != IntPtr.Zero) {
			try {
				if (PdfiumNative.FPDF_ImportPages(dest, doc, null, 0) != 0
					&& PdfiumNative.FPDF_GetPageCount(dest) > 0) {
					var viaImport = saveDocToBytesUnlocked(dest);
					if (viaImport != null && viaImport.Length >= 8)
						return viaImport;
				}
			} catch (Exception ex) {
				DocLog.Warn("ImportPages save path: " + ex.Message);
			} finally {
				try { PdfiumNative.FPDF_CloseDocument(dest); } catch { /* ignore */ }
			}
		}

		// 回退：直接 SaveAsCopy（新建文档编辑路径可用）
		var direct = saveDocToBytesUnlocked(doc);
		if (direct == null || direct.Length < 8)
			throw new InvalidOperationException("FPDF_SaveAsCopy 失败");
		return direct;
	}

	byte[] saveDocToBytesUnlocked(IntPtr document) {
		if (document == IntPtr.Zero) return null;
		using var ms = new MemoryStream();
		writeCb = (pThis, pData, size) => {
			if (size == 0 || pData == IntPtr.Zero) return 1;
			var buf = new byte[size];
			Marshal.Copy(pData, buf, 0, (int)size);
			ms.Write(buf, 0, (int)size);
			return 1;
		};
		writePin = GCHandle.Alloc(writeCb);
		try {
			var fw = new PdfiumNative.FPDF_FILEWRITE {
				version = 1,
				WriteBlock = Marshal.GetFunctionPointerForDelegate(writeCb),
			};
			if (PdfiumNative.FPDF_SaveAsCopy(document, ref fw, PdfiumNative.FPDF_NO_INCREMENTAL) == 0)
				return null;
			return ms.ToArray();
		} finally {
			if (writePin.IsAllocated) writePin.Free();
			writeCb = null;
		}
	}

	public void SaveTo(string path) {
		throwif();
		if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径无效");
		var bytes = SaveToBytes();
		var dir = Path.GetDirectoryName(path);
		var tmp = Path.Combine(string.IsNullOrEmpty(dir) ? Path.GetTempPath() : dir,
			Path.GetFileName(path) + ".~prosave.tmp");
		File.WriteAllBytes(tmp, bytes);
		if (File.Exists(path)) {
			var bak = path + ".bak";
			try { if (File.Exists(bak)) File.Delete(bak); } catch { /* ignore */ }
			try {
				File.Replace(tmp, path, bak);
				try { File.Delete(bak); } catch { /* ignore */ }
			} catch {
				File.Copy(tmp, path, true);
				try { File.Delete(tmp); } catch { /* ignore */ }
			}
		} else {
			File.Move(tmp, path);
		}
		dirty = false;
		DocLog.Info($"PdfProEngine SaveAsCopy ok path={path} size={bytes.Length}");
	}

	PdfProObject findNearest(int pageIndex, PdfProObjType type, double pdfX, double pdfY) {
		var list = ListObjects(pageIndex, forceReload: true);
		PdfProObject best = null;
		var bestD = double.MaxValue;
		foreach (var o in list) {
			if (o.Type != type) continue;
			var d = Math.Abs(o.Left - pdfX) + Math.Abs(o.Bottom - pdfY);
			if (d < bestD) { bestD = d; best = o; }
		}
		return best;
	}

	void throwif() {
		if (disposed || doc == IntPtr.Zero)
			throw new ObjectDisposedException(nameof(PdfProEngine));
	}

	public void Dispose() {
		if (disposed) return;
		disposed = true;
		lock (PdfIo.Gate) {
			closeFonts();
			closeAllPages();
			if (doc != IntPtr.Zero) {
				try { PdfiumNative.FPDF_CloseDocument(doc); } catch { /* ignore */ }
				doc = IntPtr.Zero;
			}
		}
		undoStack.Clear();
		redoStack.Clear();
		if (writePin.IsAllocated) writePin.Free();
	}
}
