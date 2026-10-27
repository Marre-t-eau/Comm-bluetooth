# Comm-bluetooth

Envoyer le son d'un PC Windows **sans Bluetooth** vers des écouteurs Bluetooth, en passant par un téléphone Android sur le même Wi-Fi.

```
PC Windows ──(Wi-Fi, Opus/UDP)──▶ Téléphone Android ──(Bluetooth)──▶ Écouteurs
```

## État d'avancement

| Étape | Contenu | État |
|---|---|---|
| 1 | Programme Windows : capture du son, compression Opus, envoi réseau, interface | ✅ |
| 2 | Application Android (.NET MAUI) : réception, tampon anti-coupures, lecture vers les écouteurs | ✅ |
| 3 | Finitions : latence, reconnexion automatique, .exe/.apk prêts à installer, guide | ✅ |

**Pour installer et utiliser l'application, voir le [guide](docs/GUIDE.md).**

## Organisation du code

| Dossier | Rôle |
|---|---|
| `src/CommBluetooth.Core` | Bibliothèque commune PC/Android : protocole réseau, conversion audio, encodage Opus, envoi/réception UDP, découverte automatique. |
| `src/CommBluetooth.Phone` | Application Android (.NET MAUI) : service de premier plan, lecture AudioTrack vers la sortie Bluetooth, interface. |
| `src/CommBluetooth.Windows` | Programme Windows (WinForms) : capture WASAPI « ce que le PC joue » et interface. |
| `tools/CommBluetooth.TestReceiver` | Récepteur de test en console : se comporte comme le téléphone et enregistre le son reçu dans un `.wav`. |
| `tests/CommBluetooth.Core.Tests` | Tests automatisés (protocole, rééchantillonnage, Opus, chaîne réseau complète). |

## Fonctionnement

* **Capture** : WASAPI en mode *loopback* sur la sortie choisie (par défaut celle de Windows). Le PC doit donc avoir une sortie audio active (haut-parleurs, prise casque…), même si rien n'y est branché.
* **Conversion** : n'importe quel format (mono, 5.1, 44,1 / 96 kHz…) est ramené en stéréo 48 kHz.
* **Compression** : Opus en trames de 10 ms (96 à 256 kb/s), via [Concentus](https://github.com/lostromb/concentus), 100 % C#.
* **Réseau** : UDP. Chaque paquet porte un identifiant de session et un numéro de séquence pour repérer les pertes. Un *keepalive* est envoyé pendant les silences.
* **Réception (téléphone)** : un tampon anti-gigue remet les trames dans l'ordre, masque les pertes avec Opus et jette l'avance excessive pour garder une latence stable (≈ 30, 50 ou 100 ms au choix, avec une marge automatique après une coupure). Le son est joué avec AudioTrack ; Android l'envoie de lui-même aux écouteurs Bluetooth connectés.
* **Découverte** : le téléphone annonce sa présence en broadcast (port UDP 50505) ; le programme Windows l'affiche dans la liste. Le son est envoyé sur le port UDP 50506.

Le détail du format des paquets est dans [`Packet.cs`](src/CommBluetooth.Core/Protocol/Packet.cs).

## Compiler et lancer

Prérequis : [SDK .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet test tests/CommBluetooth.Core.Tests
dotnet run --project src/CommBluetooth.Windows          # programme Windows
```

### Application Android

L'`.apk` est compilé automatiquement par GitHub Actions (onglet **Actions**, artefact `CommBluetooth-android`). Pour le compiler soi-même :

```powershell
dotnet workload install maui-android
dotnet publish src/CommBluetooth.Phone -c Release -f net10.0-android
```

Sur le téléphone, autorisez l'installation d'applications de source inconnue, installez l'`.apk`, connectez vos écouteurs, puis appuyez sur **Démarrer**.

### Publier une version

Onglet **Actions** → **Release** → **Run workflow** : compile le `.exe` Windows autonome et l'`.apk`, et les publie dans une Release GitHub (détails dans le [guide](docs/GUIDE.md)).

### Tester sans téléphone

Sur un autre PC du réseau (ou le même) :

```powershell
dotnet run --project tools/CommBluetooth.TestReceiver -- --out recu.wav
```

Le récepteur de test apparaît dans la liste du programme Windows. Cliquez sur **Diffuser**, jouez un son, puis Ctrl+C dans la console : `recu.wav` contient ce qui a été reçu.

### Pare-feu

Au premier lancement, Windows demande d'autoriser Comm Bluetooth sur le réseau : acceptez pour les réseaux **privés**, sinon le téléphone n'apparaîtra pas dans la liste (l'adresse IP peut toujours être saisie à la main).
