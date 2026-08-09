from __future__ import annotations

import asyncio
import shutil


class OTDConfigurator:
    def __init__(
        self,
        *,
        enabled: bool,
        tablet: str = "Apple iPad Pro (Apple Pencil)",
        output_mode: str = "OpenTabletDriver.Desktop.Output.AbsoluteMode",
        cli: str = "otd",
    ) -> None:
        self.enabled = enabled
        self.tablet = tablet
        self.output_mode = output_mode
        self.cli = cli
        self._lock = asyncio.Lock()
        self._configured = False

    async def ensure(self, *, force: bool = False) -> bool:
        if not self.enabled or self._configured and not force:
            return self._configured
        async with self._lock:
            if self._configured and not force:
                return True
            executable = shutil.which(self.cli)
            if not executable:
                print("OTD auto-config skipped: otd CLI was not found")
                return False
            for attempt in range(10):
                detected = await self._run(executable, "detect")
                configured = detected and await self._run(
                    executable, "setoutputmode", self.tablet, self.output_mode
                )
                if configured:
                    await self._run(executable, "savedefaultsettings")
                    self._configured = True
                    print(f"OTD configured: {self.tablet} -> {self.output_mode}")
                    return True
                if attempt == 0:
                    print("OTD auto-config: waiting for the virtual iPad tablet")
                await asyncio.sleep(1)
            print("OTD auto-config failed; check that the daemon and iPad plugin are running")
            return False

    @staticmethod
    async def _run(executable: str, *arguments: str) -> bool:
        try:
            process = await asyncio.create_subprocess_exec(
                executable, *arguments,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
            stdout, stderr = await asyncio.wait_for(process.communicate(), timeout=5)
            if process.returncode == 0:
                return True
            detail = (stderr or stdout).decode(errors="replace").strip()
            if detail:
                print(f"OTD {' '.join(arguments)}: {detail}")
        except (OSError, asyncio.TimeoutError) as error:
            print(f"OTD {' '.join(arguments)}: {error}")
        return False
