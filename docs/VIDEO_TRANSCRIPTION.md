# 视频链接转写

VoiceInput 支持直接转写公开的 YouTube 和 B 站视频，不需要先打开视频网页。

1. 启动应用，在“公开视频链接”框粘贴 `https://www.youtube.com/...`、`https://youtu.be/...`、`https://www.bilibili.com/...` 或 `https://b23.tv/...` 链接。
2. 点击“开始转写”，应用会依次下载音频、转换为 Whisper 音频格式并进行本地识别。
3. 识别结果可以直接编辑、删除和修改，然后复制当前内容、清空文字或保存为 TXT。
4. 转写过程中可以点击“取消转写”；下载器、转换器会被终止，本次临时目录会在任务结束时清理。

首版只处理公开可访问内容，不支持登录、付费或 DRM 内容。发布包必须保留 `tools` 目录，其中包含 `yt-dlp.exe`、`ffmpeg.exe` 和 `THIRD-PARTY-NOTICES.txt`。

从源码构建时，请先从 [yt-dlp 官方发布页](https://github.com/yt-dlp/yt-dlp/releases) 和 [FFmpeg Windows 构建发布页](https://github.com/BtbN/FFmpeg-Builds/releases) 获取对应的 Windows 可执行文件，放到仓库根目录的 `tools` 下，再构建应用。构建会把工具复制到程序输出目录。
