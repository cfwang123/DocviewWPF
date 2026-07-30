using System;

namespace DocviewWPF;

static class ViewerFactory {
	public static IDocViewer Create(DocKind kind) {
		switch (kind) {
			case DocKind.Pdf: return new PdfViewer();
			case DocKind.Docx: return new DocxViewer();
			case DocKind.Xlsx: return new XlsxViewer();
			default: throw new NotSupportedException("不支持的文件类型");
		}
	}
}
