const boot = document.getElementById("publicBoot");
const bootText = document.getElementById("publicBootText");
const bootLines = [
  "ESTABLISHING PUBLIC OPERATIONS LINK",
  "VERIFYING LAUNCHER RELEASE CHANNEL",
  "TACTICAL DOWNLOAD SYSTEM READY"
];
let bootStep = 0;
const bootTimer = setInterval(() => {
  bootText.textContent = bootLines[Math.min(bootStep, bootLines.length - 1)];
  bootStep++;
  if (bootStep > bootLines.length) {
    clearInterval(bootTimer);
    boot.classList.add("complete");
  }
}, 420);

fetch("/api/v1/release")
  .then(r => r.ok ? r.json() : Promise.reject())
  .then(release => {
    document.getElementById("releaseVersion").textContent =
      `Launcher v${release.version} · Plugin v0.1.4-alpha`;
  })
  .catch(() => {
    document.getElementById("releaseVersion").textContent =
      "Launcher release channel";
  });
