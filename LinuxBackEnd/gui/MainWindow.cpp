#include "MainWindow.h"

#include <QApplication>
#include <QCheckBox>
#include <QComboBox>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QFormLayout>
#include <QGridLayout>
#include <QGroupBox>
#include <QGuiApplication>
#include <QHBoxLayout>
#include <QLabel>
#include <QLineEdit>
#include <QMessageBox>
#include <QPlainTextEdit>
#include <QProcessEnvironment>
#include <QPushButton>
#include <QRandomGenerator>
#include <QScreen>
#include <QSettings>
#include <QSpinBox>
#include <QStandardPaths>
#include <QVBoxLayout>

#ifdef Q_OS_LINUX
#include <signal.h>
#include <sys/prctl.h>
#include <unistd.h>
#endif

namespace {
QString selectedText(const QComboBox *box)
{
    return box->currentText().trimmed();
}

bool copyExecutable(const QString &source, const QString &destination, QString *error)
{
    if (source.isEmpty() || !QFileInfo::exists(source)) {
        *error = QStringLiteral("Bundled file is missing: %1").arg(source);
        return false;
    }
    QDir().mkpath(QFileInfo(destination).absolutePath());
    QFile::remove(destination);
    if (!QFile::copy(source, destination)) {
        *error = QStringLiteral("Could not copy %1 to %2").arg(source, destination);
        return false;
    }
    QFile::setPermissions(destination, QFileDevice::ReadOwner | QFileDevice::WriteOwner |
        QFileDevice::ExeOwner | QFileDevice::ReadGroup | QFileDevice::ExeGroup |
        QFileDevice::ReadOther | QFileDevice::ExeOther);
    return true;
}
}

MainWindow::MainWindow(QWidget *parent) : QMainWindow(parent)
{
#ifdef Q_OS_LINUX
    backend_.setChildProcessModifier([] {
        // Let the backend clean up iproxy if the launcher disappears.
        ::prctl(PR_SET_PDEATHSIG, SIGTERM);
        if (::getppid() == 1) ::_exit(1);
    });
#endif
    buildUi();
    connect(&backend_, &QProcess::readyReadStandardOutput, this, &MainWindow::readBackendOutput);
    connect(&backend_, &QProcess::readyReadStandardError, this, &MainWindow::readBackendOutput);
    connect(&backend_, &QProcess::finished, this, &MainWindow::backendFinished);
    loadSettings();
    refreshScreens();
}

MainWindow::~MainWindow()
{
    saveSettings();
    stopBackend();
}

void MainWindow::buildUi()
{
    setWindowTitle(QStringLiteral("iPad Tablet Backend 0.0.3"));
    resize(780, 760);
    setMinimumSize(680, 620);

    auto *central = new QWidget(this);
    auto *root = new QVBoxLayout(central);
    auto *title = new QLabel(QStringLiteral("iPad Tablet Backend"));
    auto font = title->font();
    font.setPointSize(20);
    font.setBold(true);
    title->setFont(font);
    root->addWidget(title);
    root->addWidget(new QLabel(QStringLiteral(
        "Qt 6 launcher · encrypted UDP · USB · OpenTabletDriver")));

    auto *connection = new QGroupBox(QStringLiteral("Connection"));
    auto *connectionGrid = new QGridLayout(connection);
    udpCheck_ = new QCheckBox(QStringLiteral("Encrypted UDP"));
    udpCheck_->setChecked(true);
    usbCheck_ = new QCheckBox(QStringLiteral("USB through iproxy"));
    tokenEdit_ = new QLineEdit;
    tokenEdit_->setPlaceholderText(QStringLiteral("At least 16 UTF-8 bytes"));
    auto *generateButton = new QPushButton(QStringLiteral("Generate token"));
    connect(generateButton, &QPushButton::clicked, this, &MainWindow::generateToken);
    connectionGrid->addWidget(udpCheck_, 0, 0);
    connectionGrid->addWidget(tokenEdit_, 0, 1);
    connectionGrid->addWidget(generateButton, 0, 2);
    connectionGrid->addWidget(usbCheck_, 1, 0, 1, 3);
    root->addWidget(connection);

    auto *capture = new QGroupBox(QStringLiteral("Display and encoding"));
    auto *captureForm = new QFormLayout(capture);
    auto *screenRow = new QWidget;
    auto *screenLayout = new QHBoxLayout(screenRow);
    screenLayout->setContentsMargins(0, 0, 0, 0);
    screenBox_ = new QComboBox;
    auto *refreshButton = new QPushButton(QStringLiteral("Refresh"));
    connect(refreshButton, &QPushButton::clicked, this, &MainWindow::refreshScreens);
    screenLayout->addWidget(screenBox_, 1);
    screenLayout->addWidget(refreshButton);
    captureForm->addRow(QStringLiteral("Screen"), screenRow);

    resolutionBox_ = new QComboBox;
    resolutionBox_->setEditable(true);
    resolutionBox_->addItems({QStringLiteral("Native"), QStringLiteral("960x540"),
        QStringLiteral("1280x720"), QStringLiteral("1920x1080")});
    resolutionBox_->setCurrentText(QStringLiteral("1280x720"));
    captureForm->addRow(QStringLiteral("Stream resolution"), resolutionBox_);

    encoderBox_ = new QComboBox;
    encoderBox_->addItems({QStringLiteral("auto"), QStringLiteral("h264_vaapi"), QStringLiteral("libx264")});
    captureForm->addRow(QStringLiteral("Encoder"), encoderBox_);
    vaapiEdit_ = new QLineEdit;
    vaapiEdit_->setPlaceholderText(QStringLiteral("Automatic, e.g. /dev/dri/renderD128"));
    captureForm->addRow(QStringLiteral("VA-API device"), vaapiEdit_);

    auto *qualityRow = new QWidget;
    auto *qualityLayout = new QHBoxLayout(qualityRow);
    qualityLayout->setContentsMargins(0, 0, 0, 0);
    fpsSpin_ = new QSpinBox;
    fpsSpin_->setRange(30, 120);
    fpsSpin_->setValue(120);
    bitrateSpin_ = new QSpinBox;
    bitrateSpin_->setRange(1, 50);
    bitrateSpin_->setSuffix(QStringLiteral(" Mbit/s"));
    bitrateSpin_->setValue(8);
    rateBox_ = new QComboBox;
    rateBox_->addItems({QStringLiteral("cbr"), QStringLiteral("vbr")});
    qualityLayout->addWidget(new QLabel(QStringLiteral("FPS")));
    qualityLayout->addWidget(fpsSpin_);
    qualityLayout->addWidget(new QLabel(QStringLiteral("Bitrate")));
    qualityLayout->addWidget(bitrateSpin_);
    qualityLayout->addWidget(rateBox_);
    captureForm->addRow(QStringLiteral("Gaming profile"), qualityRow);
    root->addWidget(capture);

    auto *installation = new QGroupBox(QStringLiteral("Backend and OpenTabletDriver"));
    auto *installationLayout = new QHBoxLayout(installation);
    otdCheck_ = new QCheckBox(QStringLiteral("Configure OTD automatically"));
    otdCheck_->setChecked(true);
    auto *installButton = new QPushButton(QStringLiteral("Install / Repair"));
    installButton->setToolTip(QStringLiteral(
        "Installs the bundled backend and OTD integration. Polkit is used only for udev permissions."));
    connect(installButton, &QPushButton::clicked, this, &MainWindow::installComponents);
    installationLayout->addWidget(otdCheck_, 1);
    installationLayout->addWidget(installButton);
    root->addWidget(installation);

    auto *logGroup = new QGroupBox(QStringLiteral("Backend log"));
    auto *logLayout = new QVBoxLayout(logGroup);
    logEdit_ = new QPlainTextEdit;
    logEdit_->setReadOnly(true);
    logEdit_->setMaximumBlockCount(2000);
    logLayout->addWidget(logEdit_);
    root->addWidget(logGroup, 1);

    auto *actions = new QHBoxLayout;
    statusLabel_ = new QLabel(QStringLiteral("Stopped"));
    startButton_ = new QPushButton(QStringLiteral("Start backend"));
    stopButton_ = new QPushButton(QStringLiteral("Stop"));
    stopButton_->setEnabled(false);
    connect(startButton_, &QPushButton::clicked, this, &MainWindow::startBackend);
    connect(stopButton_, &QPushButton::clicked, this, &MainWindow::stopBackend);
    actions->addWidget(statusLabel_);
    actions->addStretch();
    actions->addWidget(stopButton_);
    actions->addWidget(startButton_);
    root->addLayout(actions);
    setCentralWidget(central);
}

void MainWindow::loadSettings()
{
    QSettings settings;
    tokenEdit_->setText(settings.value(QStringLiteral("token")).toString());
    if (tokenEdit_->text().isEmpty()) generateToken();
    resolutionBox_->setCurrentText(settings.value(QStringLiteral("resolution"), QStringLiteral("1280x720")).toString());
    encoderBox_->setCurrentText(settings.value(QStringLiteral("encoder"), QStringLiteral("auto")).toString());
    vaapiEdit_->setText(settings.value(QStringLiteral("vaapiDevice")).toString());
    fpsSpin_->setValue(settings.value(QStringLiteral("fps"), 120).toInt());
    bitrateSpin_->setValue(settings.value(QStringLiteral("bitrateMbps"), 8).toInt());
    rateBox_->setCurrentText(settings.value(QStringLiteral("rateControl"), QStringLiteral("cbr")).toString());
    udpCheck_->setChecked(settings.value(QStringLiteral("udp"), true).toBool());
    usbCheck_->setChecked(settings.value(QStringLiteral("usb"), false).toBool());
    otdCheck_->setChecked(settings.value(QStringLiteral("otd"), true).toBool());
}

void MainWindow::saveSettings() const
{
    QSettings settings;
    settings.setValue(QStringLiteral("token"), tokenEdit_->text());
    settings.setValue(QStringLiteral("resolution"), selectedText(resolutionBox_));
    settings.setValue(QStringLiteral("encoder"), selectedText(encoderBox_));
    settings.setValue(QStringLiteral("vaapiDevice"), vaapiEdit_->text().trimmed());
    settings.setValue(QStringLiteral("fps"), fpsSpin_->value());
    settings.setValue(QStringLiteral("bitrateMbps"), bitrateSpin_->value());
    settings.setValue(QStringLiteral("rateControl"), selectedText(rateBox_));
    settings.setValue(QStringLiteral("udp"), udpCheck_->isChecked());
    settings.setValue(QStringLiteral("usb"), usbCheck_->isChecked());
    settings.setValue(QStringLiteral("otd"), otdCheck_->isChecked());
}

void MainWindow::refreshScreens()
{
    const QString previous = screenBox_->currentData().toMap().value(QStringLiteral("name")).toString();
    screenBox_->clear();
    for (QScreen *screen : QGuiApplication::screens()) {
        const QRect geometry = screen->geometry();
        QVariantMap data{{QStringLiteral("name"), screen->name()},
            {QStringLiteral("x"), geometry.x()}, {QStringLiteral("y"), geometry.y()},
            {QStringLiteral("width"), geometry.width()}, {QStringLiteral("height"), geometry.height()}};
        screenBox_->addItem(QStringLiteral("%1 — %2×%3 at %4,%5")
            .arg(screen->name()).arg(geometry.width()).arg(geometry.height())
            .arg(geometry.x()).arg(geometry.y()), data);
    }
    for (int index = 0; index < screenBox_->count(); ++index) {
        if (screenBox_->itemData(index).toMap().value(QStringLiteral("name")).toString() == previous) {
            screenBox_->setCurrentIndex(index);
            break;
        }
    }
}

void MainWindow::generateToken()
{
    QByteArray random;
    random.reserve(24);
    for (int index = 0; index < 3; ++index) {
        const quint64 value = QRandomGenerator::system()->generate64();
        random.append(reinterpret_cast<const char *>(&value), sizeof(value));
    }
    tokenEdit_->setText(QString::fromLatin1(random.toHex()));
}

QString MainWindow::assetPath(const QString &relative) const
{
    const QString bundleRoot = qEnvironmentVariable("IPAD_TABLET_BUNDLE_ROOT");
    const QStringList candidates{
        bundleRoot.isEmpty() ? QString() : QDir(bundleRoot).filePath(QStringLiteral("share/ipad-tablet/") + relative),
        QDir(QCoreApplication::applicationDirPath()).filePath(QStringLiteral("../share/ipad-tablet/") + relative),
        QDir(QStringLiteral(IPAD_TABLET_SOURCE_DIR)).filePath(relative)
    };
    for (const QString &candidate : candidates)
        if (!candidate.isEmpty() && QFileInfo::exists(candidate)) return QFileInfo(candidate).absoluteFilePath();
    return {};
}

QString MainWindow::backendExecutable() const
{
    const QString override = qEnvironmentVariable("IPAD_TABLET_BACKEND_BINARY");
    const QString bundleRoot = qEnvironmentVariable("IPAD_TABLET_BUNDLE_ROOT");
    const QStringList candidates{
        override,
        assetPath(QStringLiteral("backend/ipad-tablet-backend")),
        bundleRoot.isEmpty() ? QString() : QDir(bundleRoot).filePath(QStringLiteral("lib/ipad-tablet/ipad-tablet-backend")),
        QDir::home().filePath(QStringLiteral(".local/bin/ipad-tablet-backend")),
        QStandardPaths::findExecutable(QStringLiteral("ipad-tablet-backend"))
    };
    for (const QString &candidate : candidates)
        if (!candidate.isEmpty() && QFileInfo(candidate).isExecutable()) return QFileInfo(candidate).absoluteFilePath();
    return {};
}

void MainWindow::installComponents()
{
    if (backend_.state() != QProcess::NotRunning) {
        QMessageBox::warning(this, QStringLiteral("Backend running"),
            QStringLiteral("Stop the backend before installing or repairing components."));
        return;
    }
    QString error;
    appendLog(QStringLiteral("Installing user components …"));
    if (!installUserComponents(&error) || !runPolkitInstaller(&error)) {
        appendLog(QStringLiteral("Installation failed: %1").arg(error));
        QMessageBox::critical(this, QStringLiteral("Installation failed"), error);
        return;
    }
    appendLog(QStringLiteral("Installation completed. Log out and back in if ipadtablet group membership was new."));
    QMessageBox::information(this, QStringLiteral("Installation complete"),
        QStringLiteral("Backend and iPad OTD integration are installed. If this was the first install, log out and back in once."));
}

bool MainWindow::installUserComponents(QString *error)
{
    const QString bundledBackend = assetPath(QStringLiteral("backend/ipad-tablet-backend"));
    const QString installedBackend = QDir::home().filePath(QStringLiteral(".local/bin/ipad-tablet-backend"));
    if (!copyExecutable(bundledBackend, installedBackend, error)) return false;

    const QString configuration = assetPath(QStringLiteral("otd/Apple-iPad-Pro.json"));
    const QString configRoot = QStandardPaths::writableLocation(QStandardPaths::ConfigLocation);
    const QString configDestination = QDir(configRoot).filePath(
        QStringLiteral("OpenTabletDriver/Configurations/Apple-iPad-Pro.json"));
    QDir().mkpath(QFileInfo(configDestination).absolutePath());
    QFile::remove(configDestination);
    if (configuration.isEmpty() || !QFile::copy(configuration, configDestination)) {
        *error = QStringLiteral("Could not install the OpenTabletDriver tablet configuration.");
        return false;
    }

    const QString plugin = assetPath(QStringLiteral("otd/IPadPencilHub.dll"));
    return runOtdInstaller(plugin, error);
}

bool MainWindow::runOtdInstaller(const QString &plugin, QString *error)
{
    const QString otd = QStandardPaths::findExecutable(QStringLiteral("otd"));
    if (otd.isEmpty()) {
        *error = QStringLiteral("OpenTabletDriver's 'otd' command is not installed or not in PATH.");
        return false;
    }
    if (plugin.isEmpty()) {
        *error = QStringLiteral("The bundled IPadPencilHub.dll is missing.");
        return false;
    }
    const QList<QStringList> commands{
        {QStringLiteral("installplugin"), plugin},
        {QStringLiteral("enabletools"), QStringLiteral("IPadTablet.OpenTabletDriver.IPadPencilTool")},
        {QStringLiteral("savedefaultsettings")}
    };
    for (const QStringList &arguments : commands) {
        QProcess process;
        process.start(otd, arguments);
        if (!process.waitForFinished(15'000) || process.exitStatus() != QProcess::NormalExit || process.exitCode() != 0) {
            *error = QString::fromUtf8(process.readAllStandardError()).trimmed();
            if (error->isEmpty()) *error = QStringLiteral("OTD command failed: %1").arg(arguments.join(' '));
            return false;
        }
    }
    QProcess::execute(QStringLiteral("systemctl"),
        {QStringLiteral("--user"), QStringLiteral("restart"), QStringLiteral("opentabletdriver.service")});
    return true;
}

bool MainWindow::runPolkitInstaller(QString *error)
{
    const QString pkexec = QStandardPaths::findExecutable(QStringLiteral("pkexec"));
    if (pkexec.isEmpty()) {
        *error = QStringLiteral("pkexec/polkit is required to install udev permissions.");
        return false;
    }
    const QString installedHelper = QStringLiteral("/usr/libexec/ipad-tablet/install-helper");
    const QString bundledHelper = assetPath(QStringLiteral("install/ipad-tablet-install-helper"));
    const QString helper = QFileInfo::exists(installedHelper) ? installedHelper : bundledHelper;
    if (helper.isEmpty()) {
        *error = QStringLiteral("The bundled Polkit installation helper is missing.");
        return false;
    }
    QProcess process;
    process.start(pkexec, {helper, QStringLiteral("install-system")});
    if (!process.waitForFinished(120'000) || process.exitStatus() != QProcess::NormalExit || process.exitCode() != 0) {
        const QString detail = QString::fromUtf8(process.readAllStandardError()).trimmed();
        *error = detail.isEmpty() ? QStringLiteral("Administrative installation was cancelled or failed.") : detail;
        return false;
    }
    return true;
}

void MainWindow::startBackend()
{
    if (backend_.state() != QProcess::NotRunning) return;
    saveSettings();
    if (!udpCheck_->isChecked() && !usbCheck_->isChecked()) {
        QMessageBox::warning(this, QStringLiteral("No transport"), QStringLiteral("Enable encrypted UDP, USB, or both."));
        return;
    }
    if (udpCheck_->isChecked() && tokenEdit_->text().toUtf8().size() < 16) {
        QMessageBox::warning(this, QStringLiteral("Token too short"),
            QStringLiteral("Encrypted UDP requires at least 16 UTF-8 bytes."));
        return;
    }
    const QString executable = backendExecutable();
    if (executable.isEmpty()) {
        QMessageBox::critical(this, QStringLiteral("Backend missing"),
            QStringLiteral("Install/Repair the bundled backend first."));
        return;
    }
    if (screenBox_->currentIndex() < 0) {
        QMessageBox::critical(this, QStringLiteral("No screen"), QStringLiteral("No display is available for capture."));
        return;
    }

    const QVariantMap screen = screenBox_->currentData().toMap();
    QString resolution = selectedText(resolutionBox_);
    if (resolution.compare(QStringLiteral("Native"), Qt::CaseInsensitive) == 0)
        resolution = QStringLiteral("%1x%2").arg(screen.value(QStringLiteral("width")).toInt())
            .arg(screen.value(QStringLiteral("height")).toInt());
    QStringList arguments{QStringLiteral("serve"), QStringLiteral("--resolution"), resolution,
        QStringLiteral("--fps"), QString::number(fpsSpin_->value()),
        QStringLiteral("--bitrate"), QString::number(bitrateSpin_->value() * 1'000'000),
        QStringLiteral("--rate-control"), selectedText(rateBox_),
        QStringLiteral("--encoder"), selectedText(encoderBox_)};

    const QString session = qEnvironmentVariable("XDG_SESSION_TYPE").toLower();
    if (session == QStringLiteral("x11")) {
        arguments << QStringLiteral("--source") << QStringLiteral("x11")
                  << QStringLiteral("--origin")
                  << QStringLiteral("%1,%2").arg(screen.value(QStringLiteral("x")).toInt())
                         .arg(screen.value(QStringLiteral("y")).toInt());
    } else {
        arguments << QStringLiteral("--source") << QStringLiteral("wayland")
                  << QStringLiteral("--output") << screen.value(QStringLiteral("name")).toString();
    }
    const QString vaapiDevice = vaapiEdit_->text().trimmed();
    if (!vaapiDevice.isEmpty()
        && vaapiDevice.compare(QStringLiteral("auto"), Qt::CaseInsensitive) != 0
        && vaapiDevice.compare(QStringLiteral("automatic"), Qt::CaseInsensitive) != 0)
        arguments << QStringLiteral("--vaapi-device") << vaapiDevice;
    if (!udpCheck_->isChecked()) arguments << QStringLiteral("--no-udp");
    if (usbCheck_->isChecked()) arguments << QStringLiteral("--usb");
    if (!otdCheck_->isChecked()) arguments << QStringLiteral("--no-otd-auto-config");

    auto environment = QProcessEnvironment::systemEnvironment();
    // AppRun injects Qt libraries for the launcher. Passing those libraries to
    // host tools breaks rolling distributions (for example wf-recorder loading
    // the AppImage's older libsystemd together with the host libmount).
    const QString hostLibraryPath = environment.value(QStringLiteral("IPAD_TABLET_HOST_LD_LIBRARY_PATH"));
    if (hostLibraryPath.isEmpty())
        environment.remove(QStringLiteral("LD_LIBRARY_PATH"));
    else
        environment.insert(QStringLiteral("LD_LIBRARY_PATH"), hostLibraryPath);
    environment.remove(QStringLiteral("IPAD_TABLET_HOST_LD_LIBRARY_PATH"));
    environment.remove(QStringLiteral("QT_PLUGIN_PATH"));
    environment.remove(QStringLiteral("QT_QPA_PLATFORM"));
    environment.insert(QStringLiteral("IPAD_TABLET_TOKEN"), tokenEdit_->text());
    backend_.setProcessEnvironment(environment);
    backend_.setProgram(executable);
    backend_.setArguments(arguments);
    backend_.setWorkingDirectory(QFileInfo(executable).absolutePath());
    appendLog(QStringLiteral("Starting %1 %2").arg(executable, arguments.join(' ')));
    backend_.start();
    if (!backend_.waitForStarted(5'000)) {
        QMessageBox::critical(this, QStringLiteral("Start failed"), backend_.errorString());
        return;
    }
    setRunning(true);
}

void MainWindow::stopBackend()
{
    if (backend_.state() == QProcess::NotRunning) return;
    backend_.terminate();
    if (!backend_.waitForFinished(3'000)) {
        backend_.kill();
        backend_.waitForFinished(3'000);
    }
}

void MainWindow::readBackendOutput()
{
    const QByteArray output = backend_.readAllStandardOutput() + backend_.readAllStandardError();
    for (const QByteArray &line : output.split('\n'))
        if (!line.trimmed().isEmpty()) appendLog(QString::fromUtf8(line));
}

void MainWindow::backendFinished(int exitCode, QProcess::ExitStatus status)
{
    readBackendOutput();
    appendLog(QStringLiteral("Backend stopped (exit %1, %2).")
        .arg(exitCode).arg(status == QProcess::NormalExit ? QStringLiteral("normal") : QStringLiteral("crashed")));
    setRunning(false);
}

void MainWindow::appendLog(const QString &line)
{
    logEdit_->appendPlainText(line);
}

void MainWindow::setRunning(bool running)
{
    startButton_->setEnabled(!running);
    stopButton_->setEnabled(running);
    statusLabel_->setText(running ? QStringLiteral("Running") : QStringLiteral("Stopped"));
    statusLabel_->setStyleSheet(running ? QStringLiteral("color: #22aa55; font-weight: 600") : QString());
}
