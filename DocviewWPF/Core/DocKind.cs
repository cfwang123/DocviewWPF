namespace DocviewWPF;

enum DocKind {
	Unknown = 0,
	Pdf = 1,
	Docx = 2,
	Xlsx = 3,
}

static class DocKindUtil {
	public static DocKind FromPath(string path) {
		if (string.IsNullOrWhiteSpace(path)) return DocKind.Unknown;
		var ext = System.IO.Path.GetExtension(path);
		if (string.IsNullOrEmpty(ext)) return DocKind.Unknown;
		switch (ext.ToLowerInvariant()) {
			case ".pdf": return DocKind.Pdf;
			case ".docx": return DocKind.Docx;
			case ".xlsx":
			case ".xlsm": return DocKind.Xlsx;
			default: return DocKind.Unknown;
		}
	}

	public static string Filter =>
		"文档|*.pdf;*.docx;*.xlsx;*.xlsm|PDF|*.pdf|Word|*.docx|Excel|*.xlsx;*.xlsm|所有文件|*.*";
}
