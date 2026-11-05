"""ARK 测试的公共准备。

**必须在 import app.* 之前调用 ensure_key()**：app.config 里的 settings 是
模块级单例，import 那一刻就把环境变量固化了，之后再设 ARK_API_KEY 不生效。

Key 优先取环境变量；没有就从已登录的 arkcli profile 现取（与 start-real.sh
同一套逻辑，测试里不用手工导出）。
"""
from __future__ import annotations

import json
import os
import subprocess

PROFILE = os.environ.get("ARK_CLI_PROFILE", "agent-plan_cn-beijing_personal")


def ensure_key() -> str:
    key = (os.environ.get("ARK_API_KEY") or "").strip()
    if key:
        return key
    try:
        out = subprocess.check_output(
            f"arkcli profile apikey get --profile {PROFILE} --format json",
            shell=True)
        key = (json.loads(out).get("api_key") or "").strip()
    except Exception as exc:                             # noqa: BLE001
        print(f"无法从 arkcli 取得套餐 Key：{exc}")
        return ""
    if key:
        os.environ["ARK_API_KEY"] = key
    return key


def skip(reason: str) -> int:
    print(f"SKIP：{reason}")
    return 2
