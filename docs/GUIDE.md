# Guide d'installation et d'utilisation

Comm Bluetooth envoie le son de votre PC Windows (même sans Bluetooth) vers vos écouteurs Bluetooth, en passant par votre téléphone Android connecté au même réseau.

```
PC Windows ──(réseau local)──▶ Téléphone Android ──(Bluetooth)──▶ Écouteurs
```

## 1. Télécharger

Sur la page **Releases** du dépôt, prenez la dernière version :

* `CommBluetooth-PC-x.y.z.exe` pour le PC ;
* `CommBluetooth-Android-x.y.z.apk` pour le téléphone.

## 2. Installer sur le téléphone

1. Copiez l'`.apk` sur le téléphone (ou téléchargez-le directement depuis le téléphone).
2. Ouvrez-le. Android demande d'autoriser l'installation d'applications « de source inconnue » pour l'application qui l'ouvre (navigateur, Fichiers…) : acceptez.
3. Ouvrez **Comm Bluetooth** et acceptez l'autorisation de notifications (elle sert au bouton « Arrêter » dans la notification).

## 3. Installer sur le PC

Aucune installation : rangez `CommBluetooth-PC-x.y.z.exe` où vous voulez (par exemple dans `Documents`) et double-cliquez dessus.

* **SmartScreen** (« Windows a protégé votre ordinateur ») : le fichier n'est pas signé par un éditeur payant. Cliquez sur **Informations complémentaires** puis **Exécuter quand même**.
* **Pare-feu** : au premier lancement, autorisez Comm Bluetooth sur les **réseaux privés**. Sans cela, le téléphone n'apparaîtra pas dans la liste (vous pourrez toujours saisir son adresse IP à la main).

## 4. Utilisation

1. Connectez vos écouteurs Bluetooth au téléphone, comme d'habitude.
2. Sur le téléphone, appuyez sur **Démarrer**.
3. Sur le PC, le téléphone apparaît dans la liste : sélectionnez-le et cliquez sur **Diffuser**.

C'est tout : tout ce que joue le PC arrive dans les écouteurs.

### Pour que tout se fasse tout seul

* Sur le PC, cochez **Lancer avec Windows** : le programme démarre réduit près de l'horloge à chaque ouverture de session.
* Sur le PC, gardez **Diffuser automatiquement vers le dernier téléphone** coché : dès que le téléphone apparaît, la diffusion démarre, et elle suit le téléphone si son adresse IP change.
* Sur le téléphone, activez **Démarrer à l'ouverture de l'application**.

Il suffit alors d'ouvrir l'application sur le téléphone.

## Réglages du téléphone

| Réglage | Effet |
|---|---|
| **Tampon** | Faible (≈ 30 ms), Normale (≈ 50 ms) ou Stable (≈ 100 ms). Plus petit = moins de décalage mais plus de risque de coupure. Le tampon grossit de lui-même après une coupure, puis redescend. |
| **Garder l'écran allumé** | Android ne garde le Wi-Fi vraiment réactif qu'écran allumé ; utile si le son hache écran éteint. |
| **Démarrer à l'ouverture** | Lance la réception dès l'ouverture de l'application. |
| **Mode développeur** | Affiche le détail de la latence : espacement des paquets réseau, marge automatique, tampon Android et part du Bluetooth. |

## À propos du décalage

Le décalage total s'affiche sur le téléphone. Il se compose de :

* **la partie Comm Bluetooth** (capture sur le PC, réseau, tampon) : environ 100 ms ;
* **le Bluetooth des écouteurs** : souvent 150 à 300 ms selon le modèle. Cette partie ne dépend pas de l'application et ne peut pas être réduite par elle.

Pour regarder une vidéo, un lecteur comme **VLC** permet de décaler le son pour le resynchroniser avec l'image (touches **J** et **K**).

## En cas de problème

| Problème | Solution |
|---|---|
| Le téléphone n'apparaît pas sur le PC | Vérifiez que l'application est démarrée sur le téléphone, que les deux appareils sont sur le même réseau, et que le pare-feu Windows autorise Comm Bluetooth (Paramètres → Confidentialité et sécurité → Sécurité Windows → Pare-feu → Autoriser une application). Sinon, saisissez l'adresse IP affichée sur le téléphone. |
| « aucun son joué sur le PC » | Rien n'est en cours de lecture, ou la mauvaise sortie est choisie dans « Son à envoyer ». |
| Le son hache | Passez le tampon sur « Stable », activez « Garder l'écran allumé », et si possible connectez le téléphone au Wi-Fi 5 GHz de la box. |
| Le son sort aussi des haut-parleurs du PC | C'est normal : le PC capture ce qu'il joue sur sa sortie. Éteignez les haut-parleurs. Si vous préférez baisser le volume de Windows, vérifiez que le son arrive toujours au téléphone : selon les PC, la capture suit ou non ce volume. |
| L'installation d'une nouvelle version de l'`.apk` échoue | Désinstallez d'abord l'ancienne version (voir ci-dessous). |

## Pour le développeur : signer l'`.apk` avec une clé fixe

Sans clé fixe, chaque version publiée est signée avec une clé différente, et Android refuse alors de mettre à jour l'application par-dessus l'ancienne. Pour l'éviter, une seule fois :

```bash
keytool -genkeypair -v -keystore commbluetooth.keystore -alias commbluetooth \
        -keyalg RSA -keysize 2048 -validity 10000
base64 -w0 commbluetooth.keystore   # copier le résultat
```

Puis dans **Settings → Secrets and variables → Actions** du dépôt, créez :

* `ANDROID_KEYSTORE_BASE64` : le texte base64 obtenu ;
* `ANDROID_KEYSTORE_PASSWORD` : le mot de passe choisi.

Gardez le fichier `commbluetooth.keystore` en lieu sûr, hors du dépôt.

## Publier une nouvelle version

Onglet **Actions** → **Release** → **Run workflow**, saisissez le numéro de version (par exemple `1.0.0`). La Release est créée avec le `.exe` et l'`.apk`.
