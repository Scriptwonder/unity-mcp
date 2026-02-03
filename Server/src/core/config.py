"""
Configuration settings for the Unity MCP CLI bridge server.
"""

from dataclasses import dataclass
import logging


@dataclass
class ServerConfig:
    """Configuration for the CLI bridge server."""

    # Logging settings
    log_level: str = "INFO"
    log_format: str = "%(asctime)s - %(name)s - %(levelname)s - %(message)s"

    def configure_logging(self) -> None:
        level = getattr(logging, self.log_level, logging.INFO)
        logging.basicConfig(level=level, format=self.log_format)


# Create a global config instance
config = ServerConfig()
