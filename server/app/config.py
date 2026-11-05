"""配置。全部走环境变量，不落盘密钥。"""
from __future__ import annotations

import os
from dataclasses import dataclass, field


def _env(name: str, default: str = "") -> str:
    return os.environ.get(name, default).strip()


def _env_int(name: str, default: int) -> int:
    try:
        return int(os.environ.get(name, "") or default)
    except ValueError:
        return default


def _env_float(name: str, default: float) -> float:
    try:
        return float(os.environ.get(name, "") or default)
    except ValueError:
        return default


@dataclass
class Settings:
    # ---- 上游：任何 Anthropic Messages 兼容中转 ----
    # 例：https://api.deepseek.com/anthropic  或  自建 one-api / new-api 的 /v1
    upstream_base_url: str = field(default_factory=lambda: _env("UPSTREAM_BASE_URL"))
    upstream_api_key: str = field(default_factory=lambda: _env("UPSTREAM_API_KEY"))
    # 客户端发来的 model 名（aliyun.qwen3.8-flash 之类）统一映射成上游真实模型
    upstream_model: str = field(
        default_factory=lambda: _env("UPSTREAM_MODEL", "deepseek-v4.1-flash"))
    upstream_timeout: int = field(default_factory=lambda: _env_int("UPSTREAM_TIMEOUT", 300))
    upstream_extra_headers: dict[str, str] = field(default_factory=dict)

    # ---- 推理强度（对语音体验影响最大的一个开关）----
    # 实测 spark-x2.6（推理模型）：开思考时首正文 4716ms，关掉 1112ms，差 4.2 倍。
    # 语音助手是先出声再慢慢说，首字延迟比总时长重要得多。
    #   smart = 需要模型主动调工具的回合开 1024 token，其余关掉（默认）
    #           关掉思考时模型只有约 20% 的概率真的调用 capture_screen，
    #           其余回合只叙述「我去截一张」就结束，客户端表现成「没回信」。
    #   off   = 一律关（最快，但会让截图/指点类回合失效）
    #   on    = 不干预，让上游默认思考
    #   auto  = 按客户端传来的 main_reasoning_effort 映射成 budget_tokens
    upstream_thinking: str = field(
        default_factory=lambda: _env("UPSTREAM_THINKING", "smart").lower())

    # ---- 本地 ----
    host: str = field(default_factory=lambda: _env("HOST", "0.0.0.0"))
    port: int = field(default_factory=lambda: _env_int("PORT", 8787))
    # 不配就全放行：客户端带的是真实 JWT，自建服务器不校验签名，只看有没有
    require_auth: bool = field(
        default_factory=lambda: _env("REQUIRE_AUTH", "0") in ("1", "true", "yes"))
    # mock 模式：不连上游，本地回一段固定文本，用来验证协议层
    mock_mode: bool = field(
        default_factory=lambda: _env("MOCK_MODE", "0") in ("1", "true", "yes"))
    log_bodies: bool = field(
        default_factory=lambda: _env("LOG_BODIES", "1") in ("1", "true", "yes"))
    # P1 的 ASR 是空壳，不会真识别。给个固定文本，让点点能走到 /chat。
    # 设成空串则退回「什么都不说」（点点不会触发对话）。
    asr_stub_text: str = field(
        default_factory=lambda: _env("ASR_STUB_TEXT", "看看我的屏幕"))
    # 热键按下了、音频也送到了，但里面没有人说话（麦克风静音/录到虚拟声卡）。
    # 这时必须和「识别失败」区分开：
    #   桩文本「看看我的屏幕」会让模型去截屏，用户完全不知道发生了什么；
    #   一句「没听到声音」才能让用户明白该检查麦克风。
    # 实测本机三个录音设备全是虚拟设备，喂给 SenseVoice 会幻觉出「我。」，
    # 于是客户端收到一句无意义的用户输入后自行取消（response_failed cancelled=true）。
    asr_silence_text: str = field(
        default_factory=lambda: _env("ASR_SILENCE_TEXT", "（没有听到声音，请再说一遍）"))

    # ---- 图片输入策略 ----
    # 点点会把 1280x720 截图 base64 塞进 tool_result。上游不一定能看图：
    #   auto  = 有本地 OCR 就 OCR，没有就诚实告知（推荐）
    #   ocr   = 强制本地 OCR 成文本，绝不把图片发给上游
    #   pass  = 原样透传（仅当上游确实支持视觉）
    #   strip = 丢掉图片，只留一句「看不到画面，不要猜」
    # 背景：spark-x2.6 对图片是**静默丢弃**（HTTP 200，模型反问用户屏幕上是什么），
    # 不报错。若直接 pass，模型会基于「看不见」编造屏幕内容，比报错更危险。
    vision_mode: str = field(
        default_factory=lambda: _env("VISION_MODE", "auto").lower())
    # OCR 文本上限，防止整屏文字把上下文吃光
    ocr_max_chars: int = field(default_factory=lambda: _env_int("OCR_MAX_CHARS", 4000))
    # 本地 OCR 后端：
    #   auto    = 有 PaddleOCR-VL 就用它，否则退回 Windows OCR（推荐）
    #   paddle  = 只用 PaddleOCR-VL（GPU，6-14 秒，文字准得多）
    #   windows = 只用 Windows OCR（0.5 秒，但形近字错得离谱）
    # 实测同一张 1280x720 截图：
    #   Windows OCR → 「点点 Windows 予 贞 石 开 · M3 OS 集 成 spike」
    #   PaddleOCR-VL → 「点点 Windows 预研·M3 OS 集成 spike」（6-11 秒）
    ocr_backend: str = field(
        default_factory=lambda: _env("OCR_BACKEND", "auto").lower())

    # ---- 上游视觉模型（商汤 SenseNova，OpenAI 兼容 /v1/chat/completions）----
    # 为什么单独配一个上游：文本上游 spark-x2.6 只吃文本（/v1/models 只有它一个，
    # 没有任何视觉模型）。要真识图就必须另找一个多模态端点。
    #
    # 实测（evidence/selfcheck-capture-1931.jpg，1280x720）：
    #   Windows OCR  → 「点点 Windows 予 贞 石 开 · M3 OS 集 成 spike」（形近字全错）
    #   SenseNova VL → 「点点 Windows 预研 - M3 OS 集成 spike」（正确）
    #   而且 VL 能说清布局：「黑色胶囊悬浮窗显示『在听...』并附有音频波形」
    #
    # 代价是慢：15-25 秒。这个模型的推理关不干净（reasoning_effort=none 仍有
    # 500-900 推理 token），而**推理给少了它会认错界面**（实测把点点认成
    # Windows 相册 / Edge）。所以默认留足预算，宁慢勿错。
    vision_api_key: str = field(
        default_factory=lambda: _env("VISION_API_KEY", ""))
    vision_base_url: str = field(
        default_factory=lambda: _env("VISION_BASE_URL", "https://token.sensenova.cn/v1"))
    vision_model: str = field(
        default_factory=lambda: _env("VISION_MODEL", "sensenova-6.8-flash-lite"))
    # 单次视觉调用超时。实测同一张图在 14-46 秒之间波动，跟图片尺寸无关
    # （1280px 46s / 1024px 14.8s / 800px 21.4s，三次都识别正确），
    # 完全由上游负载决定。超过这个时间就放弃 VL 回退 OCR，别让用户干等。
    vision_timeout: float = field(
        default_factory=lambda: float(_env("VISION_TIMEOUT", "60") or 60))
    # 上传前把长边压到这个尺寸。实测图片从 196KB 压到 35KB，延迟几乎不变
    # （瓶颈是推理不是传输），但能省带宽，也避免超出上游的图片大小限制。
    vision_max_side: int = field(
        default_factory=lambda: _env_int("VISION_MAX_SIDE", 1280))
    # 视觉模型的推理预算。这个参数是准确率和延迟的直接旋钮，实测（同一张图）：
    #   none     8.1s   → 把点点认成「Edge 浏览器」✗
    #   low     10.9s   → 认成「Windows 相册」✗
    #   minimal 16-37s  → 3 次里 1 次认成「VS Code 文本文件」✗
    #   不传    19-40s  → 稳定准确 ✓
    # 空字符串 = 不传该字段，让模型自己决定推理量。
    # 宁可慢 10 秒，也不能给出错误的界面描述——错误的识图比「看不到」更危险。
    vision_effort: str = field(
        default_factory=lambda: _env("VISION_EFFORT", ""))

    # ---- Agent Plan：语音与识图（套餐内，不走按量）----
    # 这里所有端点只认**套餐专属 Key**（profile 绑定的那把，形如 ark-xxx）。
    # 它与 platform 按量 Key 不能互借：plan 的语音/识图端点会直接 401/404。
    #
    # 实测（2026-10-28）：
    #   识图  api/plan/v3/chat/completions + doubao-seed-2-0-mini  → 2.1s
    #   TTS   openspeech…/plan/tts/unidirectional                 → 首包 0.6s / 全文 0.87s
    #   ASR   只有 WS 二进制协议，没有 HTTP 入口                    → 走 arkcli 子进程
    #
    # 识图为什么不用 arkcli：CLI 的 +understand/+chat 对图像一律 404（走的是
    # Responses 通道），而直连 chat/completions 完全正常，还省掉 435ms 子进程启动。
    ark_api_key: str = field(default_factory=lambda: _env("ARK_API_KEY"))
    ark_plan_base_url: str = field(
        default_factory=lambda: _env(
            "ARK_PLAN_BASE_URL", "https://ark.cn-beijing.volces.com/api/plan/v3"))
    # 方舟的 Anthropic 兼容端点（不带 /v3）。客户端在设置里选了方舟的模型时，
    # 对话就走这里 —— 客户端发什么 model 就用什么 model，不再被 UPSTREAM_MODEL 覆盖。
    # 注意它和上面的 ark_plan_base_url 不是一个协议：/api/plan 是 Anthropic Messages，
    # /api/plan/v3 是 OpenAI chat/completions（识图那条路在用后者）。
    ark_anthropic_base_url: str = field(
        default_factory=lambda: _env(
            "ARK_ANTHROPIC_BASE_URL", "https://ark.cn-beijing.volces.com/api/plan"))
    # VL 档走哪个上游：
    #   auto      = 有 ARK Key 就用 doubao-seed-2-0-mini，失败回退 SenseNova
    #   ark       = 只用 ARK
    #   sensenova = 只用 SenseNova（旧行为）
    # 注意这和上面的 vision_mode 不是一回事：vision_mode 决定「VL / OCR / 透传」，
    # 这里只决定 VL 档由谁实现。
    vision_vl_backend: str = field(
        default_factory=lambda: _env("VISION_VL_BACKEND", "auto").lower())
    ark_vision_model: str = field(
        default_factory=lambda: _env("ARK_VISION_MODEL", "doubao-seed-2-0-mini-260428"))
    # 关闭深度思考。实测该模型 minimal 的 reasoning_tokens=0，描述仍然准确
    # （2.1s 认出「点点（预研）·M3 spike」和顶部的「在听...」）。
    # 这条链路只做截图描述，不需要推理链，开思考只是纯延迟。
    ark_vision_effort: str = field(
        default_factory=lambda: _env("ARK_VISION_EFFORT", "minimal"))
    ark_vision_timeout: float = field(
        default_factory=lambda: _env_float("ARK_VISION_TIMEOUT", 30.0))

    ark_tts_url: str = field(
        default_factory=lambda: _env(
            "ARK_TTS_URL",
            "https://openspeech.bytedance.com/api/v3/plan/tts/unidirectional"))
    ark_tts_resource_id: str = field(
        default_factory=lambda: _env("ARK_TTS_RESOURCE_ID", "seed-tts-2.0"))
    # 默认音色。套餐这个资源下实测可用的只有 vv 与 xiaohe 两个，
    # voice_map 里没登记的 voice_id 一律落到这里。
    ark_tts_speaker: str = field(
        default_factory=lambda: _env("ARK_TTS_SPEAKER", "zh_female_vv_uranus_bigtts"))
    # 单次合成超时。实测正常 0.6-2s（28 字约 1.2s，58 字约 1.9s），
    # 定 15s 意味着链路卡住时用户要干等 15 秒静音才回退 SAPI（10-28 15:42 实测 15.8s）。
    # 6s 已经覆盖到约 180 字，超时后 ark_tts 会重试一次，两次都失败才回退。
    ark_tts_timeout: float = field(
        default_factory=lambda: _env_float("ARK_TTS_TIMEOUT", 6.0))

    # ASR 后端：local = SenseVoice（默认，58ms）；doubao = arkcli 子进程（约 2.1s）；
    # qwen3 = 本地 llama.cpp Vulkan（Qwen3-ASR-1.7B Q8_0，约 0.2s，最准）。
    # qwen3 与 doubao 一样没有 partial（整段识别）；qwen3 失败回退 local。
    asr_backend: str = field(
        default_factory=lambda: _env("ASR_BACKEND", "local").lower())
    arkcli_bin: str = field(
        default_factory=lambda: _env("ARKCLI_BIN", "arkcli"))
    asr_doubao_timeout: float = field(
        default_factory=lambda: _env_float("ASR_DOUBAO_TIMEOUT", 90.0))
    # Qwen3-ASR 的 llama-server 地址与单次超时。正常推理 0.2s，
    # 超时默认 30s：首次请求带 Vulkan 着色器编译要 13s 左右，别压得太紧。
    asr_qwen3_base_url: str = field(
        default_factory=lambda: _env("ASR_QWEN3_BASE_URL", "http://127.0.0.1:8930"))
    asr_qwen3_timeout: float = field(
        default_factory=lambda: _env_float("ASR_QWEN3_TIMEOUT", 30.0))

    def check(self) -> list[str]:
        problems = []
        if not self.mock_mode:
            if not self.upstream_base_url:
                problems.append("UPSTREAM_BASE_URL 未设置（或设 MOCK_MODE=1 本地自测）")
            if not self.upstream_api_key:
                problems.append("UPSTREAM_API_KEY 未设置")
        return problems


settings = Settings()
