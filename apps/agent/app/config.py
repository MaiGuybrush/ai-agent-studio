"""Python 設定檔（spec 2.1/2.3）：模型端點與 key、C# → Python 的 API key。

預設讀 apps/agent/config.toml（不進 git）；範本見 config.example.toml。環境變數 AGENT_CONFIG 可指定其他路徑。
"""
import os
import tomllib
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


@dataclass(frozen=True)
class Config:
    api_key: str = ""  # 空字串 = 拒絕所有 /runs（fail closed）
    model_name: str = ""
    model_base_url: str = ""
    model_api_key: str = ""


def load_config(path: Path | None = None) -> Config:
    p = path or Path(os.environ.get("AGENT_CONFIG", ROOT / "config.toml"))
    if not p.exists():
        return Config()
    raw = tomllib.loads(p.read_text(encoding="utf-8"))
    m = raw.get("model", {})
    return Config(
        api_key=raw.get("api_key", ""),
        model_name=m.get("name", ""),
        model_base_url=m.get("base_url", ""),
        model_api_key=m.get("api_key", ""),
    )
