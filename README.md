# VITTLES' Ranked Stats

Personal DotA 2 ranked match stats tracker.

**Live Dashboard:** https://victualler.github.io/ranked-stats/

## Technical Details

* **GitHub Pages** – Hosts the static dashboard and renders MMR progression charts using Chart.js.
* **GitHub Actions** – Runs the headless C# sync job on a daily schedule.
* **SteamKit2** – Communicates directly with the Steam network and Dota 2 Game Coordinator to retrieve match history and rank changes.
* **AWS SSM** – Securely manages, retrieves, and rotates persistent Steam authentication tokens via IAM OIDC.

## Credits

Based on [ShowMMR](https://github.com/Lypheo/ShowMMR) by Lypheo, originally created by AveYo.

## License

Distributed under the [MIT License](LICENSE).
