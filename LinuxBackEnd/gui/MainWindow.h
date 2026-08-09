#pragma once

#include <QMainWindow>
#include <QProcess>

class QCheckBox;
class QComboBox;
class QLabel;
class QLineEdit;
class QPlainTextEdit;
class QPushButton;
class QSpinBox;

class MainWindow final : public QMainWindow
{
    Q_OBJECT

public:
    explicit MainWindow(QWidget *parent = nullptr);
    ~MainWindow() override;

private slots:
    void refreshScreens();
    void generateToken();
    void installComponents();
    void startBackend();
    void stopBackend();
    void readBackendOutput();
    void backendFinished(int exitCode, QProcess::ExitStatus status);

private:
    void buildUi();
    void loadSettings();
    void saveSettings() const;
    void appendLog(const QString &line);
    void setRunning(bool running);
    QString backendExecutable() const;
    QString assetPath(const QString &relative) const;
    bool installUserComponents(QString *error);
    bool runPolkitInstaller(QString *error);
    bool runOtdInstaller(const QString &plugin, QString *error);

    QProcess backend_;
    QComboBox *screenBox_ = nullptr;
    QComboBox *resolutionBox_ = nullptr;
    QComboBox *encoderBox_ = nullptr;
    QComboBox *rateBox_ = nullptr;
    QLineEdit *tokenEdit_ = nullptr;
    QLineEdit *vaapiEdit_ = nullptr;
    QSpinBox *fpsSpin_ = nullptr;
    QSpinBox *bitrateSpin_ = nullptr;
    QCheckBox *udpCheck_ = nullptr;
    QCheckBox *usbCheck_ = nullptr;
    QCheckBox *otdCheck_ = nullptr;
    QPlainTextEdit *logEdit_ = nullptr;
    QLabel *statusLabel_ = nullptr;
    QPushButton *startButton_ = nullptr;
    QPushButton *stopButton_ = nullptr;
};
