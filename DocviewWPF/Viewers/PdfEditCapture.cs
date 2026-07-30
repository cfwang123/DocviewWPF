using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;

namespace DocviewWPF;

/// <summary>从 PDF 页命中已有文字/图片，转为可编辑覆盖对象（共享给主查看器与专业编辑窗）。</summary>
static class PdfEditCapture {
	/// <summary>点 (xPt,yPt) 页坐标（左上原点 Y 向下 pt）。优先图，再文字。</summary>
	public static PdfEditItem TryCapture(PdfiumSession session, int page, double xPt, double yPt,
		string defaultFont = "Microsoft YaHei", double defaultFontSize = 12,
		MediaColor? defaultFore = null) {
		if (session == null || page < 0) return null;
		var img = TryCaptureImage(session, page, xPt, yPt);
		if (img != null) return img;
		return TryCaptureText(session, page, xPt, yPt, defaultFont, defaultFontSize, defaultFore);
	}

	public static PdfEditItem TryCaptureImage(PdfiumSession session, int page, double xPt, double yPt) {
		if (session == null) return null;
		List<PdfImageInfo> imgs = null;
		try {
			PdfIo.WithLock(() => {
				if (session == null) return;
				imgs = session.ListImageBounds(page);
			});
		} catch (Exception ex) {
			DocLog.Warn($"PdfEditCapture image: {ex.Message}");
			return null;
		}
		if (imgs == null || imgs.Count == 0) return null;

		PdfImageInfo best = null;
		var bestArea = double.MaxValue;
		foreach (var im in imgs) {
			if (xPt < im.Left - 1 || xPt > im.Right + 1 || yPt < im.Top - 1 || yPt > im.Bottom + 1)
				continue;
			var area = Math.Max(1, (im.Right - im.Left) * (im.Bottom - im.Top));
			if (area < bestArea) {
				bestArea = area;
				best = im;
			}
		}
		if (best == null) return null;

		BitmapSource bmp = null;
		var idx = best.ObjectIndex;
		try {
			PdfIo.WithLock(() => {
				if (session == null) return;
				bmp = session.ExtractImageBitmap(page, idx);
			});
		} catch (Exception ex) {
			DocLog.Warn($"PdfEditCapture extract: {ex.Message}");
			return null;
		}
		if (bmp == null) return null;

		byte[] png;
		try {
			var enc = new PngBitmapEncoder();
			enc.Frames.Add(BitmapFrame.Create(bmp));
			using var ms = new MemoryStream();
			enc.Save(ms);
			png = ms.ToArray();
		} catch {
			return null;
		}

		var pad = 0.5;
		return new PdfEditItem {
			Page = page,
			Kind = PdfEditKind.Image,
			X = Math.Max(0, best.Left - pad),
			Y = Math.Max(0, best.Top - pad),
			W = Math.Max(4, best.Right - best.Left + pad * 2),
			H = Math.Max(4, best.Bottom - best.Top + pad * 2),
			ImagePng = png,
			// 标记：保存时先画白底再画图
			BackColor = MediaColor.FromRgb(0xFF, 0xFF, 0xFF),
		};
	}

	public static PdfEditItem TryCaptureText(PdfiumSession session, int page, double xPt, double yPt,
		string defaultFont, double defaultFontSize, MediaColor? defaultFore) {
		if (session == null) return null;
		List<PdfCharInfo> chars = null;
		try {
			PdfIo.WithLock(() => {
				if (session == null) return;
				chars = session.ExtractChars(page);
			});
		} catch (Exception ex) {
			DocLog.Warn($"PdfEditCapture text: {ex.Message}");
			return null;
		}
		if (chars == null || chars.Count == 0) return null;

		PdfCharInfo best = null;
		var bestD = 36.0 * 36.0;
		foreach (var ch in chars) {
			var cx = (ch.Left + ch.Right) * 0.5;
			var cy = (ch.Top + ch.Bottom) * 0.5;
			var dx = cx - xPt;
			var dy = cy - yPt;
			var d = dx * dx + dy * dy;
			if (xPt >= ch.Left - 1 && xPt <= ch.Right + 1 && yPt >= ch.Top - 1 && yPt <= ch.Bottom + 1)
				d = 0;
			if (d < bestD) {
				bestD = d;
				best = ch;
			}
		}
		if (best == null) return null;

		var lineY = (best.Top + best.Bottom) * 0.5;
		var lineH = Math.Max(4, best.Bottom - best.Top);
		var line = chars
			.Where(ch => Math.Abs((ch.Top + ch.Bottom) * 0.5 - lineY) <= lineH * 0.55)
			.OrderBy(ch => ch.Left)
			.ToList();
		var bi = line.FindIndex(c => c.Index == best.Index);
		if (bi < 0) bi = 0;
		var lo = bi;
		var hi = bi;
		while (lo > 0 && line[lo].Left - line[lo - 1].Right <= lineH * 0.8) lo--;
		while (hi < line.Count - 1 && line[hi + 1].Left - line[hi].Right <= lineH * 0.8) hi++;
		if (hi - lo < 1) {
			lo = bi;
			hi = bi;
			while (lo > 0 && line[lo].Left - line[lo - 1].Right < lineH * 2) lo--;
			while (hi < line.Count - 1 && line[hi + 1].Left - line[hi].Right < lineH * 2) hi++;
		}

		double minL = double.MaxValue, minT = double.MaxValue, maxR = double.MinValue, maxB = double.MinValue;
		var sb = new StringBuilder();
		for (var i = lo; i <= hi; i++) {
			var ch = line[i];
			sb.Append(ch.Char);
			if (ch.Left < minL) minL = ch.Left;
			if (ch.Top < minT) minT = ch.Top;
			if (ch.Right > maxR) maxR = ch.Right;
			if (ch.Bottom > maxB) maxB = ch.Bottom;
		}
		if (minL >= maxR) return null;
		var pad = 2.0;
		var fontSz = Math.Max(8, maxB - minT);
		return new PdfEditItem {
			Page = page,
			Kind = PdfEditKind.Text,
			X = Math.Max(0, minL - pad),
			Y = Math.Max(0, minT - pad),
			W = Math.Max(20, maxR - minL + pad * 2),
			H = Math.Max(12, maxB - minT + pad * 2),
			Text = sb.ToString(),
			FontName = string.IsNullOrWhiteSpace(defaultFont) ? "Microsoft YaHei" : defaultFont,
			FontSizePt = fontSz > 1 ? fontSz : defaultFontSize,
			ForeColor = defaultFore ?? MediaColor.FromRgb(0x11, 0x18, 0x27),
			BackColor = MediaColor.FromRgb(0xFF, 0xFF, 0xFF),
		};
	}

	/// <summary>选区字符索引 → 编辑项。</summary>
	public static PdfEditItem FromCharRange(List<PdfCharInfo> chars, int page, int start, int end,
		string defaultFont, double defaultFontSize, MediaColor defaultFore) {
		if (chars == null || chars.Count == 0) return null;
		var a = Math.Min(start, end);
		var b = Math.Max(start, end);
		double minL = double.MaxValue, minT = double.MaxValue, maxR = double.MinValue, maxB = double.MinValue;
		var sb = new StringBuilder();
		foreach (var ch in chars) {
			if (ch.Index < a || ch.Index > b) continue;
			sb.Append(ch.Char);
			if (ch.Left < minL) minL = ch.Left;
			if (ch.Top < minT) minT = ch.Top;
			if (ch.Right > maxR) maxR = ch.Right;
			if (ch.Bottom > maxB) maxB = ch.Bottom;
		}
		if (minL >= maxR) return null;
		var pad = 2.0;
		return new PdfEditItem {
			Page = page,
			Kind = PdfEditKind.Text,
			X = Math.Max(0, minL - pad),
			Y = Math.Max(0, minT - pad),
			W = Math.Max(20, maxR - minL + pad * 2),
			H = Math.Max(12, maxB - minT + pad * 2),
			Text = sb.ToString(),
			FontName = defaultFont,
			FontSizePt = Math.Max(8, maxB - minT),
			ForeColor = defaultFore,
			BackColor = MediaColor.FromRgb(0xFF, 0xFF, 0xFF),
		};
	}
}
